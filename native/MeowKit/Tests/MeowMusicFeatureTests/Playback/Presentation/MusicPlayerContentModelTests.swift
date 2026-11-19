#if os(iOS)
import Foundation
import XCTest
import UIKit
import Combine
import MeowUI
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicPlayerContentModelTests: XCTestCase {
    func testPreviewRenditionIsDisplayedBeforeSelectionSettlesAndSurvivesRepositoryProjection() async throws {
        let entered = expectation(description: "Preview success registration is held")
        let gate = MusicCollectionGate { entered.fulfill() }
        let current = ContentArtworkResponse(data: png(.red))
        let original = ContentArtworkResponse(length: MusicArtworkHTTP.maximumBytes + 1, held: true)
        let boundedURL = try XCTUnwrap(URL(string: original.url.absoluteString + "?imageView&thumbnail=1024x1024"))
        let boundedData = png(.blue)
        let bounded = ContentArtworkResponse(url: boundedURL, data: boundedData)
        let fixture = ContentArtworkFixture(current: current.url, previous: original.url,
                                            next: original.url, selectionGate: gate)
        addTeardownBlock { @MainActor in
            await gate.release()
            current.close(); original.close(); bounded.close()
            await fixture.close()
        }
        let state = fixture.model.state, fence = fixture.content.fence
        let references = fixture.model.queue.map(\.track.ref)
        await fulfillment(of: [entered], timeout: 3)
        await wait("Current cover is ready while preview registration is held") { fixture.content.artwork != nil }
        let currentImage = try XCTUnwrap(fixture.content.artwork)
        let previewImage = try XCTUnwrap(fixture.images.cached(boundedURL))
        let previous = try XCTUnwrap(fixture.model.recordPreview(.previous))
        let next = try XCTUnwrap(fixture.model.recordPreview(.next))
        XCTAssertEqual(previous.track.artwork, original.url)
        XCTAssertEqual(next.track.artwork, original.url)
        XCTAssertTrue(fixture.content.artwork(for: previous) === previewImage)
        XCTAssertTrue(fixture.content.artwork(for: next) === previewImage)
        XCTAssertNil(fixture.images.cached(original.url), "A rendition cannot masquerade as its original resource")

        fixture.model.metadataState.lyricState = "none"
        await updates()
        XCTAssertTrue(fixture.content.artwork(for: previous) === previewImage,
                      "Reconciliation must retain the successful actual resource before repository publication")
        XCTAssertTrue(fixture.content.artwork(for: next) === previewImage)
        XCTAssertTrue(fixture.content.artwork === currentImage)
        await gate.release()
        await fixture.content.flushArtwork()
        await wait("Both preview references project the committed real resource") {
            fixture.model.recordPreview(.previous)?.track.artwork == boundedURL &&
                fixture.model.recordPreview(.next)?.track.artwork == boundedURL
        }
        let successes = await fixture.service.successCalls()
        XCTAssertEqual(successes.count, 2)
        XCTAssertEqual(Set(successes.map(\.songID)), Set([previous.track.songId, next.track.songId]))
        XCTAssertTrue(successes.allSatisfy { $0.url == boundedURL && $0.requested == original.url })
        XCTAssertTrue(fixture.content.artwork(for: fixture.model.recordPreview(.previous)) === previewImage)
        XCTAssertTrue(fixture.content.artwork(for: fixture.model.recordPreview(.next)) === previewImage)

        fixture.presentation.setFullPlayerPresented(false)
        await updates()
        fixture.presentation.setFullPlayerPresented(true)
        await updates()
        await fixture.content.flushArtwork()
        XCTAssertTrue(fixture.content.artwork(for: fixture.model.recordPreview(.previous)) === previewImage)
        XCTAssertTrue(fixture.content.artwork(for: fixture.model.recordPreview(.next)) === previewImage)
        let originalBytes = await fixture.cache.read(original.url)
        let boundedBytes = await fixture.cache.read(boundedURL)
        XCTAssertNil(originalBytes)
        XCTAssertEqual(boundedBytes?.data, boundedData)
        XCTAssertEqual(original.requests.map(\.url), [original.url])
        XCTAssertEqual(bounded.requests.map(\.url), [boundedURL])
        XCTAssertEqual(current.requests.map(\.url), [current.url])
        XCTAssertEqual(fixture.model.state, state)
        XCTAssertEqual(fixture.content.fence, fence)
        XCTAssertEqual(fixture.model.queue.map(\.track.ref), references)
        XCTAssertEqual(fixture.model.position, 17)
        XCTAssertTrue(fixture.content.artwork === currentImage)
    }

    func testSameSongRenditionWaitsForReadinessAndFailureOrClearKeepsThePreviousImage() async throws {
        for outcome in [ContentArtworkOutcome.ready, .failed, .cleared] {
            let entered = expectation(description: "Replacement rendition requested: \(outcome)")
            let current = ContentArtworkResponse(data: png(.red))
            let original = ContentArtworkResponse(length: MusicArtworkHTTP.maximumBytes + 1, held: true)
            let boundedURL = try XCTUnwrap(URL(string: original.url.absoluteString + "?imageView&thumbnail=1024x1024"))
            let bounded = ContentArtworkResponse(url: boundedURL,
                data: outcome == .failed ? Data([1, 2, 3]) : png(.blue),
                held: true, started: { entered.fulfill() })
            let fixture = ContentArtworkFixture(current: current.url)
            addTeardownBlock { @MainActor in
                current.close(); original.close(); bounded.close()
                await fixture.close()
            }
            await wait("Initial current cover is ready") { fixture.content.artwork != nil }
            fixture.model.beginPlaybackMetadata()
            await fixture.model.playbackMetadata.flush()
            await updates()
            let previousImage = try XCTUnwrap(fixture.content.artwork)
            let state = fixture.model.state, fence = fixture.content.fence
            let references = fixture.model.queue.map(\.track.ref)
            let songID = try XCTUnwrap(fixture.model.current?.track.songId)
            var displayed: [UIImage?] = [previousImage]
            let observation = fixture.content.artworkChanges.sink { displayed.append(fixture.content.artwork) }
            defer { observation.cancel() }

            try await fixture.service.replaceArtwork(songID: songID, url: original.url)
            await fulfillment(of: [entered], timeout: 3)
            await updates()
            XCTAssertEqual(fixture.model.displayTrack?.artwork, original.url)
            XCTAssertTrue(fixture.content.artwork === previousImage, "A held same-song replacement cannot flash empty")
            XCTAssertTrue(displayed.allSatisfy { $0 === previousImage })
            if outcome == .cleared { await fixture.cache.clearImages() }
            bounded.finish()
            await fixture.content.flushArtwork()
            await fixture.model.playbackMetadata.flush()
            await updates()
            fixture.model.metadataState.lyricState = "none"
            await updates()
            await fixture.content.flushArtwork()
            let successes = await fixture.service.successCalls()
            let failures = await fixture.service.failureCount()
            let stored = await fixture.cache.read(boundedURL)
            if outcome == .ready {
                await wait("Only the ready real resource replaces the old cover") {
                    fixture.model.displayTrack?.artwork == boundedURL &&
                        fixture.content.artwork === fixture.images.cached(boundedURL)
                }
                let ready = try XCTUnwrap(fixture.images.cached(boundedURL))
                XCTAssertFalse(ready === previousImage)
                XCTAssertTrue(displayed.allSatisfy { $0 === previousImage || $0 === ready })
                XCTAssertEqual(successes, [.init(songID: songID, url: boundedURL, requested: original.url)])
                XCTAssertEqual(failures, 0)
                XCTAssertEqual(stored?.data, bounded.data)
            } else {
                XCTAssertTrue(fixture.content.artwork === previousImage)
                XCTAssertTrue(displayed.allSatisfy { $0 === previousImage })
                XCTAssertTrue(successes.isEmpty, "Unfinished or failed rendition must not be registered")
                XCTAssertEqual(failures, outcome == .cleared ? 0 : 1,
                               "Cache cancellation is not a real candidate failure")
                XCTAssertNil(stored)
                XCTAssertNil(fixture.images.cached(boundedURL))
            }
            XCTAssertNil(fixture.images.cached(original.url))
            XCTAssertEqual(current.requests.map(\.url), [current.url])
            XCTAssertEqual(original.requests.map(\.url), [original.url])
            XCTAssertEqual(bounded.requests.map(\.url), [boundedURL])
            XCTAssertEqual(fixture.model.state, state)
            XCTAssertEqual(fixture.content.fence, fence)
            XCTAssertEqual(fixture.model.queue.map(\.track.ref), references)
            XCTAssertEqual(fixture.model.position, 17)

            if outcome == .failed {
                fixture.presentation.setFullPlayerPresented(false)
                await updates()
                fixture.presentation.setFullPlayerPresented(true)
                fixture.model.metadataState.lyricState = "none"
                await updates()
                await fixture.content.flushArtwork()
                await fixture.model.playbackMetadata.flush()
                let failuresAfterReopening = await fixture.service.failureCount()
                XCTAssertEqual(failuresAfterReopening, 1)
                XCTAssertEqual(original.requests.map(\.url), [original.url],
                               "Presentation changes cannot hot-retry a real failure")
                XCTAssertEqual(bounded.requests.map(\.url), [boundedURL])
                XCTAssertTrue(fixture.content.artwork === previousImage)
            }

            fixture.model.playbackState.state = .init(revision: state.revision + 1,
                currentId: "next", total: 3)
            await updates()
            XCTAssertNil(fixture.content.artwork, "A different permanent song cannot inherit the retained cover")
        }
    }

    func testClearedPreviewRenditionDoesNotPublishRegisterOrRefillAndKeepsTheCurrentImage() async throws {
        let entered = expectation(description: "Preview rendition is held")
        let current = ContentArtworkResponse(data: png(.red))
        let original = ContentArtworkResponse(length: MusicArtworkHTTP.maximumBytes + 1, held: true)
        let boundedURL = try XCTUnwrap(URL(string: original.url.absoluteString + "?imageView&thumbnail=1024x1024"))
        let bounded = ContentArtworkResponse(url: boundedURL, data: png(.blue),
            held: true, started: { entered.fulfill() })
        let fixture = ContentArtworkFixture(current: current.url, previous: original.url, next: original.url)
        addTeardownBlock { @MainActor in
            current.close(); original.close(); bounded.close()
            await fixture.close()
        }
        let state = fixture.model.state, fence = fixture.content.fence
        await fulfillment(of: [entered], timeout: 3)
        await wait("Current image is ready beside the held preview") { fixture.content.artwork != nil }
        let currentImage = try XCTUnwrap(fixture.content.artwork)
        await fixture.cache.clearImages()
        bounded.finish()
        await fixture.content.flushArtwork()
        fixture.model.metadataState.lyricState = "none"
        await updates()
        await fixture.content.flushArtwork()
        let successes = await fixture.service.successCalls()
        let failures = await fixture.service.failureCount()
        let originalBytes = await fixture.cache.read(original.url)
        let boundedBytes = await fixture.cache.read(boundedURL)
        XCTAssertNil(fixture.content.artwork(for: fixture.model.recordPreview(.previous)))
        XCTAssertNil(fixture.content.artwork(for: fixture.model.recordPreview(.next)))
        XCTAssertTrue(fixture.content.artwork === currentImage)
        XCTAssertTrue(successes.isEmpty)
        XCTAssertEqual(failures, 0)
        XCTAssertNil(originalBytes); XCTAssertNil(boundedBytes)
        XCTAssertNil(fixture.images.cached(original.url)); XCTAssertNil(fixture.images.cached(boundedURL))
        XCTAssertEqual(original.requests.map(\.url), [original.url])
        XCTAssertEqual(bounded.requests.map(\.url), [boundedURL])
        XCTAssertEqual(current.requests.map(\.url), [current.url])
        XCTAssertEqual(fixture.model.state, state)
        XCTAssertEqual(fixture.content.fence, fence)
        XCTAssertEqual(fixture.model.position, 17)
    }

    func testClearedCurrentImageReloadsOnceOnlyAfterForegroundRecovery() async throws {
        let cover = ContentArtworkResponse(data: png(.blue), held: true)
        let fixture = ContentArtworkFixture(current: cover.url)
        addTeardownBlock { @MainActor in
            cover.close()
            await fixture.close()
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await wait("Initial current image request is held") { cover.requests.count == 1 }
        let state = fixture.model.state, fence = fixture.content.fence
        let references = fixture.model.queue.map(\.track.ref)
        await fixture.cache.clearImages()
        cover.finish()
        await fixture.content.flushArtwork()
        fixture.model.metadataState.lyricState = "none"
        fixture.model.clock.position = 17
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await updates()
        await fixture.content.flushArtwork()
        let cancelledBytes = await fixture.cache.read(cover.url)
        let cancelledSuccesses = await fixture.service.successCalls()
        let cancelledFailures = await fixture.service.failureCount()
        XCTAssertNil(fixture.content.artwork)
        XCTAssertNil(fixture.images.cached(cover.url))
        XCTAssertNil(cancelledBytes)
        XCTAssertTrue(cancelledSuccesses.isEmpty)
        XCTAssertEqual(cancelledFailures, 0)
        XCTAssertEqual(cover.requests.map(\.url), [cover.url],
                       "Cancellation and ordinary updates cannot refill the old demand")

        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await updates()
        XCTAssertEqual(cover.requests.count, 1)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await wait("Returning to the foreground requests the same current URL once") { cover.requests.count == 2 }
        XCTAssertNil(fixture.content.artwork, "The new demand still waits for a complete image")
        fixture.model.metadataState.lyricState = "none"
        await updates()
        XCTAssertEqual(cover.requests.count, 2)
        cover.finish()
        await fixture.content.flushArtwork()
        await wait("The recovered current image is ready") { fixture.content.artwork != nil }
        let ready = try XCTUnwrap(fixture.images.cached(cover.url))
        XCTAssertTrue(fixture.content.artwork === ready)
        let recoveredBytes = await fixture.cache.read(cover.url)
        let failures = await fixture.service.failureCount()
        XCTAssertEqual(recoveredBytes?.data, cover.data)
        XCTAssertEqual(failures, 0)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        fixture.model.metadataState.lyricState = "none"
        await updates()
        await fixture.content.flushArtwork()
        XCTAssertEqual(cover.requests.map(\.url), [cover.url, cover.url])
        XCTAssertEqual(fixture.model.displayTrack?.artwork, cover.url)
        XCTAssertEqual(fixture.model.state, state)
        XCTAssertEqual(fixture.content.fence, fence)
        XCTAssertEqual(fixture.model.queue.map(\.track.ref), references)
        XCTAssertEqual(fixture.model.position, 17)
    }

    func testClearedReplacementReloadsOnceOnFullPlayerReopeningAndKeepsThePreviousImageUntilReady() async throws {
        let current = ContentArtworkResponse(data: png(.red))
        let replacement = ContentArtworkResponse(data: png(.blue), held: true)
        let fixture = ContentArtworkFixture(current: current.url)
        addTeardownBlock { @MainActor in
            current.close(); replacement.close()
            await fixture.close()
        }
        await wait("The original current image is ready") { fixture.content.artwork != nil }
        fixture.model.beginPlaybackMetadata()
        await fixture.model.playbackMetadata.flush()
        await updates()
        let previousImage = try XCTUnwrap(fixture.content.artwork)
        let songID = try XCTUnwrap(fixture.model.current?.track.songId)
        let state = fixture.model.state, fence = fixture.content.fence
        let references = fixture.model.queue.map(\.track.ref)
        var displayed: [UIImage?] = [previousImage]
        let observation = fixture.content.artworkChanges.sink { displayed.append(fixture.content.artwork) }
        defer { observation.cancel() }
        try await fixture.service.replaceArtwork(songID: songID, url: replacement.url)
        await wait("Same-song replacement is held") { replacement.requests.count == 1 }
        await fixture.cache.clearImages()
        replacement.finish()
        await fixture.content.flushArtwork()
        await fixture.model.playbackMetadata.flush()
        fixture.model.metadataState.lyricState = "none"
        await updates()
        await fixture.content.flushArtwork()
        let cancelledBytes = await fixture.cache.read(replacement.url)
        let cancelledFailures = await fixture.service.failureCount()
        XCTAssertNil(cancelledBytes)
        XCTAssertNil(fixture.images.cached(replacement.url))
        XCTAssertEqual(cancelledFailures, 0)
        XCTAssertEqual(replacement.requests.map(\.url), [replacement.url])
        XCTAssertTrue(fixture.content.artwork === previousImage)
        XCTAssertTrue(displayed.allSatisfy { $0 === previousImage })

        fixture.presentation.setFullPlayerPresented(false)
        await updates()
        XCTAssertEqual(replacement.requests.count, 1, "Closing alone is not new visible artwork demand")
        fixture.presentation.setFullPlayerPresented(true)
        await wait("Reopening requests the same replacement URL once") { replacement.requests.count == 2 }
        XCTAssertTrue(fixture.content.artwork === previousImage)
        XCTAssertTrue(displayed.allSatisfy { $0 === previousImage })
        fixture.presentation.setFullPlayerPresented(true)
        fixture.model.metadataState.lyricState = "none"
        await updates()
        XCTAssertEqual(replacement.requests.count, 2)
        replacement.finish()
        await fixture.content.flushArtwork()
        await fixture.model.playbackMetadata.flush()
        await wait("The recovered replacement becomes current") {
            fixture.content.artwork != nil && fixture.content.artwork === fixture.images.cached(replacement.url)
        }
        let ready = try XCTUnwrap(fixture.content.artwork)
        XCTAssertFalse(ready === previousImage)
        XCTAssertTrue(displayed.allSatisfy { $0 === previousImage || $0 === ready })
        let recoveredBytes = await fixture.cache.read(replacement.url)
        let failures = await fixture.service.failureCount()
        XCTAssertEqual(recoveredBytes?.data, replacement.data)
        XCTAssertEqual(failures, 0)
        fixture.presentation.setFullPlayerPresented(false)
        await updates()
        fixture.presentation.setFullPlayerPresented(true)
        fixture.model.metadataState.lyricState = "none"
        await updates()
        await fixture.content.flushArtwork()
        XCTAssertTrue(fixture.content.artwork === ready)
        XCTAssertEqual(current.requests.map(\.url), [current.url])
        XCTAssertEqual(replacement.requests.map(\.url), [replacement.url, replacement.url])
        XCTAssertEqual(fixture.model.displayTrack?.artwork, replacement.url)
        XCTAssertEqual(fixture.model.state, state)
        XCTAssertEqual(fixture.content.fence, fence)
        XCTAssertEqual(fixture.model.queue.map(\.track.ref), references)
        XCTAssertEqual(fixture.model.position, 17)
    }

    func testReopeningBeforeCancelledReadDrainsPreservesOneFreshDemandAndThePreviousImage() async throws {
        let original = ContentArtworkResponse(data: png(.red))
        let replacement = ContentArtworkResponse(data: png(.blue), held: true)
        let fixture = ContentArtworkFixture(current: original.url)
        addTeardownBlock { @MainActor in
            original.close(); replacement.close()
            await fixture.close()
        }
        await wait("The original current image is ready") { fixture.content.artwork != nil }
        fixture.model.beginPlaybackMetadata()
        await fixture.model.playbackMetadata.flush()
        await updates()
        let previousImage = try XCTUnwrap(fixture.content.artwork)
        let songID = try XCTUnwrap(fixture.model.current?.track.songId)
        let state = fixture.model.state, fence = fixture.content.fence
        let references = fixture.model.queue.map(\.track.ref)
        try await fixture.service.replaceArtwork(songID: songID, url: replacement.url)
        await wait("The old replacement read is held") { replacement.requests.count == 1 }
        await fixture.cache.clearImages()
        fixture.presentation.setFullPlayerPresented(false)
        await updates()
        fixture.presentation.setFullPlayerPresented(true)
        await updates()
        XCTAssertEqual(replacement.requests.count, 1, "New demand must wait for the old physical read")
        XCTAssertTrue(fixture.content.artwork === previousImage)

        replacement.finish()
        await wait("The new visible demand reads once after the old work drains") { replacement.requests.count == 2 }
        let beforeReady = await fixture.cache.read(replacement.url)
        let failuresBeforeReady = await fixture.service.failureCount()
        XCTAssertNil(beforeReady, "The old cleared work cannot refill the cache")
        XCTAssertEqual(failuresBeforeReady, 0)
        XCTAssertTrue(fixture.content.artwork === previousImage)
        fixture.model.metadataState.lyricState = "none"
        fixture.presentation.setFullPlayerPresented(true)
        await updates()
        XCTAssertEqual(replacement.requests.count, 2)

        replacement.finish()
        await fixture.content.flushArtwork()
        await fixture.model.playbackMetadata.flush()
        await wait("The fresh image becomes current") {
            fixture.content.artwork != nil && fixture.content.artwork === fixture.images.cached(replacement.url)
        }
        let ready = try XCTUnwrap(fixture.content.artwork)
        let recovered = await fixture.cache.read(replacement.url)
        let failures = await fixture.service.failureCount()
        XCTAssertFalse(ready === previousImage)
        XCTAssertEqual(recovered?.data, replacement.data)
        XCTAssertEqual(failures, 0)
        XCTAssertEqual(original.requests.map(\.url), [original.url])
        XCTAssertEqual(replacement.requests.map(\.url), [replacement.url, replacement.url])
        XCTAssertEqual(fixture.model.state, state)
        XCTAssertEqual(fixture.content.fence, fence)
        XCTAssertEqual(fixture.model.queue.map(\.track.ref), references)
        XCTAssertEqual(fixture.model.position, 17)
    }

    private func png(_ color: UIColor) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 2, height: 2)).pngData {
            color.setFill(); $0.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        }
    }
    private func updates() async {
        await withCheckedContinuation { continuation in RunLoop.main.perform { continuation.resume() } }
    }
    private func wait(_ description: String, until ready: () -> Bool) async {
        let deadline = Date().addingTimeInterval(3)
        while !ready(), Date() < deadline { try? await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(ready(), description)
    }
}

private enum ContentArtworkOutcome: Equatable { case ready, failed, cleared }

@MainActor
private final class ContentArtworkFixture {
    let service: ContentArtworkService
    let cache: ContentArtworkCache
    let images: MusicArtworkImages
    let model: MusicViewModel
    let playback: PlaybackController
    let presentation: MusicMiniPlayerPresentation
    let content: MusicPlayerContentModel

    init(current: URL, previous: URL? = nil, next: URL? = nil, selectionGate: MusicCollectionGate? = nil) {
        let entries = [
            Self.entry("previous", songID: "D1000000-0000-4000-8000-000000000001", url: previous),
            Self.entry("current", songID: "D1000000-0000-4000-8000-000000000002", url: current),
            Self.entry("next", songID: "D1000000-0000-4000-8000-000000000003", url: next)
        ]
        let state = MusicPlaybackState(revision: 7, settingsRevision: 3, lyricsEnabled: false,
            currentId: "current", total: 3, position: 17, canNext: true, canPrevious: true,
            nextPreviewID: "next", previousPreviewID: "previous")
        let service = ContentArtworkService(entries: entries, state: state, selectionGate: selectionGate)
        let cache = ContentArtworkCache()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ContentArtworkProtocol.self]
        let images = MusicArtworkImages(http: MusicArtworkHTTP(configuration: configuration))
        images.configure(cache: cache, selector: service)
        let playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        model.playbackState.queue = entries; model.playbackState.state = state; model.clock.position = 17
        let appearance = MusicAppearancePreferencesStore()
        let presentation = MusicMiniPlayerPresentation()
        presentation.setFullPlayerPresented(true)
        let content = MusicPlayerContentModel(model: model, playback: playback, appearance: appearance,
            presentation: presentation, artworkImages: images)
        self.service = service; self.cache = cache; self.images = images; self.model = model
        self.playback = playback; self.presentation = presentation; self.content = content
    }
    private static func entry(_ id: String, songID: String, url: URL?) -> MusicQueueEntry {
        .init(id: id, track: .init(ref: .init(kind: .queue, id: id), songId: songID,
            title: "Synthetic " + id, artist: "Synthetic artist", duration: 180, artwork: url))
    }
    func close() async {
        await model.close(); await images.close(); await content.flushArtwork()
        await service.finish()
        playback.stop()
    }
}

private actor ContentArtworkService: MusicServing, MusicArtworkSelecting, MusicSongRepositoryReading {
    struct Success: Equatable, Sendable { let songID: String; let url: URL; let requested: URL }
    private let entries: [MusicQueueEntry]
    private let snapshot: MusicPlaybackState
    private var songs: [String: MusicRemoteTrack]
    private var observers: [UUID: AsyncStream<Set<String>>.Continuation] = [:]
    private var selectionGate: MusicCollectionGate?
    private var successes: [Success] = []
    private var failures = 0
    init(entries: [MusicQueueEntry], state: MusicPlaybackState, selectionGate: MusicCollectionGate?) {
        self.entries = entries; snapshot = state; self.selectionGate = selectionGate
        songs = Dictionary(uniqueKeysWithValues: entries.map { ($0.track.songId, $0.track) })
    }
    func successCalls() -> [Success] { successes }
    func failureCount() -> Int { failures }
    func succeededArtwork(songID: String, url: URL, replacing requested: URL) async throws -> URL? {
        successes.append(.init(songID: songID, url: url, requested: requested))
        if let gate = selectionGate { selectionGate = nil; await gate.wait() }
        try Task.checkCancellation()
        guard let track = songs[songID] else { throw MusicServiceError.notFound }
        guard track.artwork == requested else { return track.artwork }
        try replaceArtwork(songID: songID, url: url)
        return url
    }
    func failedArtwork(songID: String, url: URL, attempted: Set<URL>) -> URL? { failures += 1; return nil }
    func replaceArtwork(songID: String, url: URL) throws {
        guard let track = songs[songID] else { throw MusicServiceError.notFound }
        songs[songID] = .init(ref: track.ref, songId: track.songId, title: track.title,
            artist: track.artist, album: track.album, duration: track.duration, sources: track.sources,
            artwork: url, artistNames: track.artistNames, sourcePosition: track.sourcePosition)
        for observer in observers.values { observer.yield([songID]) }
    }
    func repositoryTracks(songIDs: Set<String>) -> [String: MusicRemoteTrack] {
        songs.filter { songIDs.contains($0.key) }
    }
    func songChanges() -> AsyncStream<Set<String>> {
        let id = UUID(), pair = AsyncStream<Set<String>>.makeStream()
        observers[id] = pair.continuation
        pair.continuation.onTermination = { [weak self] _ in Task { await self?.removeObserver(id) } }
        return pair.stream
    }
    private func removeObserver(_ id: UUID) { observers[id] = nil }
    func finish() {
        for observer in observers.values { observer.finish() }
        observers.removeAll()
    }
    func state() -> MusicPlaybackState { snapshot }
    func queue() -> MusicQueueSnapshot {
        .init(revision: snapshot.revision, currentId: snapshot.currentId, entries: entries.map {
            .init(id: $0.id, track: $0.track.projected(songs))
        })
    }
    func metadataPreferences() -> MusicMetadataPreferences {
        .init(revision: 3, platforms: [.netease], enabledPlatforms: [.netease])
    }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates {
        guard let entry = entries.first(where: { $0.id == occurrenceID }),
              let track = songs[entry.track.songId] else { throw MusicServiceError.notFound }
        return .init(occurrenceID: occurrenceID, queueRevision: snapshot.revision,
            preferences: metadataPreferences(), track: track, candidates: [])
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability {
        .init(options: [], scriptState: .notConfigured)
    }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private final class ContentArtworkCache: MusicPresentationCaching, @unchecked Sendable {
    private let lock = NSLock()
    private var imageRevision = UUID()
    private var bytes: [URL: ArtworkBytes] = [:]
    let lyricRevision = UUID()
    var revision: UUID { lock.withLock { imageRevision } }
    func read(_ url: URL) async -> ArtworkBytes? { lock.withLock { bytes[url] } }
    func store(_ data: Data, for url: URL, revision: UUID) async -> ArtworkBytes? {
        lock.withLock {
            guard revision == imageRevision else { return nil }
            let value = ArtworkBytes(data: data, expiresAt: Date().addingTimeInterval(30 * 86_400))
            bytes[url] = value; return value
        }
    }
    func clearImages() async { lock.withLock { imageRevision = UUID(); bytes.removeAll() } }
    func lyrics(songID: String, authorization: MusicQueueAuthorization) async -> MusicLyricDisplaySnapshot? { nil }
    func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                     revision: UUID, authorization: MusicQueueAuthorization) async { XCTFail("No lyric writes in artwork tests") }
    func played(songID: String, artwork: URL?, confirmedAt: Date) async { XCTFail("No media playback in artwork tests") }
    func clearLyrics() async { XCTFail("No lyric clears in artwork tests") }
    func clear() async { await clearImages(); await clearLyrics() }
}

private final class ContentArtworkResponse: @unchecked Sendable {
    let url: URL
    let data: Data
    private let length: Int?
    private let held: Bool
    private let started: (@Sendable () -> Void)?
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    private var active: URLProtocol?
    var requests: [URLRequest] { lock.withLock { seen } }
    init(url: URL? = nil, data: Data = Data(), length: Int? = nil, held: Bool = false,
         started: (@Sendable () -> Void)? = nil) {
        self.url = url ?? URL(string: "https://p4.music.126.net/synthetic-\(UUID().uuidString).png")!
        self.data = data; self.length = length; self.held = held; self.started = started
        ContentArtworkProtocol.register(self)
    }
    func respond(_ instance: URLProtocol) {
        lock.withLock { seen.append(instance.request); if held { active = instance } }
        var headers = ["Content-Type": "image/png", "X-Content-Type-Options": "nosniff"]
        if let length { headers["Content-Length"] = String(length) }
        let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: headers)!
        instance.client?.urlProtocol(instance, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !data.isEmpty { instance.client?.urlProtocol(instance, didLoad: data) }
        started?()
        if !held { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func finish() {
        let instance = lock.withLock { let value = active; active = nil; return value }
        if let instance { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func stop() { lock.withLock { active = nil } }
    func close() { finish(); ContentArtworkProtocol.remove(url) }
}

private final class ContentArtworkProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var responses: [URL: ContentArtworkResponse] = [:]
    private var response: ContentArtworkResponse?
    static func register(_ response: ContentArtworkResponse) { lock.withLock { responses[response.url] = response } }
    static func remove(_ url: URL) { lock.withLock { responses[url] = nil } }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        response = Self.lock.withLock { request.url.flatMap { Self.responses[$0] } }
        guard let response else {
            XCTFail("Unexpected artwork request")
            client?.urlProtocol(self, didFailWithError: URLError(.resourceUnavailable))
            return
        }
        response.respond(self)
    }
    override func stopLoading() { response?.stop() }
}
#endif
