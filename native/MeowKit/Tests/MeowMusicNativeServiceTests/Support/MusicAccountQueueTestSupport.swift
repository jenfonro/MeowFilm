import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

actor MusicAccountQueuePages {
    private var calls: [String] = []
    private var roots: [[MusicNativeTrackData]]
    private var pages: [String: MusicNativeTrackPage]
    private var rootCursor: String?
    private let rootTotal: Int?
    private var rootIndex = 0
    private var failCursor: String?
    private var gate: NativeMusicGate?
    private var gateCursor: String?
    init(roots: [[MusicNativeTrackData]], pages: [String: MusicNativeTrackPage] = [:], rootCursor: String? = nil,
         rootTotal: Int? = nil) {
        self.roots = roots; self.pages = pages; self.rootCursor = rootCursor; self.rootTotal = rootTotal
    }
    func read(_ cursor: String?) async throws -> MusicNativeTrackPage {
        let key = cursor ?? "root"; calls.append(key)
        if key == gateCursor, let gate { await gate.wait() }
        if key == failCursor { throw MusicServiceError.upstreamFailed }
        if let cursor {
            guard let page = pages[cursor] else { throw MusicServiceError.staleResult }
            return page
        }
        guard rootIndex < roots.count else { throw MusicServiceError.upstreamInvalid }
        let rows = roots[rootIndex]; rootIndex += 1
        return .init(items: rows, total: rootTotal ?? (rootCursor == nil ? rows.count : 0), cursor: rootCursor)
    }
    func fail(_ cursor: String?) { failCursor = cursor }
    func hold(_ cursor: String, gate: NativeMusicGate) { gateCursor = cursor; self.gate = gate }
    func events() -> [String] { calls }
}

enum MusicAccountQueueFixture {
    static func source(_ pages: MusicAccountQueuePages, kind: MusicRemoteQueueContext.Kind = .playlist,
                       authorization: MusicQueueAuthorization = .init()) -> MusicRemoteQueueSource {
        .init(context: .init(kind: kind, id: "9001", title: "Synthetic remote list", platform: "tx"),
              authorization: authorization, loadPage: { try await pages.read($0) })
    }
    static func seed(_ service: NativeMusicService) async throws -> MusicQueueSnapshot {
        let page = try await service.search(platform: "tx", query: "Old", page: 1)
        _ = try await service.changeQueue(.replace([page.items[0].ref, page.items[0].ref], index: 1), expectedRevision: 0)
        return try await service.queue()
    }
    static func start(_ service: NativeMusicService, _ source: MusicRemoteQueueSource, index: Int = 0) async throws -> MusicRemoteQueueStart {
        let page = try await service.remoteQueuePage(source, cursor: nil)
        return .init(index: index, track: page.items[index])
    }
}

struct MusicAccountQueueOpeningFixture: MusicNativeAccountProviding, MusicNativeAccountQueueProviding {
    let gate = NativeMusicGate()
    let ended = NativeMusicGate()
    let lease = MusicAccountQueueLease(platform: .qq)
    private let base = NativeMusicAccountFixture()
    init() {}
    func beginAccountQueue(platform: MusicAccountPlatform) async throws -> MusicAccountQueueLease {
        await gate.wait() // Deliberately ignores task cancellation.
        return lease
    }
    func checkAccountQueue(_ value: MusicAccountQueueLease) async throws {
        guard value === lease else { throw MusicServiceError.staleResult }
        try value.authorization.check()
    }
    func accountQueuePage(_ value: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                          cursor: String?) async throws -> MusicNativeTrackPage {
        try await checkAccountQueue(value)
        return .init(items: [NativeMusicFixture.track()])
    }
    func endAccountQueue(_ value: MusicAccountQueueLease) async {
        guard value === lease else { return }
        value.revoke(); await ended.release()
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage {
        try await base.nativeRecommendations(platform: platform, kind: kind, cursor: cursor)
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        await base.nativeCollections(platform: platform, cursor: cursor)
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicNativeTrackPage {
        await base.nativeTracks(platform: platform, collectionID: collectionID, cursor: cursor)
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        await base.nativeOptions(platform: platform, nativeID: nativeID, sourceKey: sourceKey)
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        try await base.nativeResolve(platform: platform, nativeID: nativeID, selection: selection)
    }
}
