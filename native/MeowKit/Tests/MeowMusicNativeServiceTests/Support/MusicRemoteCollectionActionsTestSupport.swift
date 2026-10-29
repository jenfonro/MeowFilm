import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

// Deterministic synthetic rows only. No reference JavaScript, credentials,
// network, device, runtime host or remote account writes are involved.
enum MusicRemoteCollectionActionsFixture {
    static func track(_ id: String, title: String? = nil, artist: String = "Fixture",
                      album: String = "Synthetic album") -> MusicNativeTrackData {
        .init(platform: "tx", data: MJ.obj([
            ("musicId", .string(id)), ("songmid", .string(id)),
            ("name", .string(title ?? "Synthetic " + id)), ("singer", .string(artist)),
            ("album", .string(album)), ("duration", .n(180))
        ]).bytes)
    }
    static func pages(_ rows: [MusicNativeTrackData]) -> MusicAccountQueuePages {
        var pages: [String: MusicNativeTrackPage] = [:]
        for offset in stride(from: 30, to: rows.count, by: 30) {
            let end = min(offset + 30, rows.count)
            pages[String(offset)] = .init(items: Array(rows[offset..<end]), total: rows.count,
                cursor: end < rows.count ? String(end) : nil)
        }
        return MusicAccountQueuePages(roots: [Array(rows.prefix(30))], pages: pages,
            rootCursor: rows.count > 30 ? "30" : nil)
    }
    static func source(_ pages: MusicAccountQueuePages,
                       kind: MusicRemoteQueueContext.Kind = .playlist,
                       probe: MusicRemoteCollectionActionsProbe = .init()) -> MusicRemoteQueueSource {
        .init(context: .init(kind: kind, id: "remote-list", title: "Remote collection", platform: "tx"),
            loadPage: { try await pages.read($0) },
            validate: { try await probe.validate() },
            close: { await probe.close() })
    }
    static func nativeIDs(_ rows: [MusicNativeTrackData]) throws -> [String] {
        try rows.map { try MJ.parse($0.data)["musicId"].string }
    }
}

actor MusicRemoteCollectionActionsProbe {
    private var validationError: MusicServiceError?
    private var gate: NativeMusicGate?
    private var gateAt: Int?
    private var validations = 0
    private var closes = 0
    func fail(_ error: MusicServiceError?) { validationError = error }
    func holdValidation(_ call: Int, gate: NativeMusicGate) { gateAt = call; self.gate = gate }
    func validate() async throws {
        validations += 1
        if validations == gateAt, let gate { await gate.wait() }
        if let validationError { throw validationError }
    }
    func close() { closes += 1 }
    func closeCount() -> Int { closes }
}
