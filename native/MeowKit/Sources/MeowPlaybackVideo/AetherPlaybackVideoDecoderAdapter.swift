import Foundation
import MeowContracts
import MeowPlayback
import MeowAetherKernel

extension AetherVideoSession: PlaybackVideoOutput {}

@MainActor
final class AetherPlaybackVideoDecoderAdapter: PlaybackVideoDecoding {
    private let session: AetherVideoSession
    var engine: PlaybackVideoEngine { .aether }
    var output: any PlaybackVideoOutput { session }
    var position: Double { session.position }
    var duration: Double? { session.duration }
    var chapters: [PlaybackVideoChapter] {
        session.chapters.map { PlaybackVideoChapter(title: $0.title, start: $0.start, end: $0.end) }
    }
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)? { didSet { session.onProgress = onProgress } }
    var onTransportRate: (@MainActor (Float) -> Void)? { didSet { session.onTransportRate = onTransportRate } }
    var onPlaybackIntent: (@MainActor (Bool) -> Void)? { didSet { session.onPlaybackIntent = onPlaybackIntent } }
    var onEnd: (@MainActor () -> Void)? { didSet { session.onEnd = onEnd } }

    init() throws {
        session = try AetherVideoSession()
        session.onFailure = { [weak self] in self?.onFailure?(Self.error($0)) }
    }
    func prepare(url: URL, format: String?, clearKeys: [String: Data],
                 position: Double, headers: [String: String]) async throws {
        do {
            try await session.prepare(url: url, format: format, clearKeys: clearKeys,
                                      position: position, headers: headers)
        } catch { throw Self.error(error) }
    }
    func align(to position: Double) async throws {
        do { try await session.align(to: position) } catch { throw Self.error(error) }
    }
    func renderFirstVideoFrame() async throws {
        do { try await session.renderFirstVideoFrame() } catch { throw Self.error(error) }
    }
    func validateForCommit() throws {
        do { try session.validateForCommit() } catch { throw Self.error(error) }
    }
    func commit(rate: Float, volume: Float) { session.commit(rate: rate, volume: volume) }
    func setRate(_ rate: Float) { session.setRate(rate) }
    func seek(to position: Double) async throws -> Double {
        do { return try await session.seek(to: position) } catch { throw Self.error(error) }
    }
    func cancel() { session.cancel() }
    func close() async { await session.close() }

    private static func error(_ error: Error) -> PlaybackVideoError {
        if error is CancellationError { return .cancelled }
        switch error as? AetherVideoFailure {
        case .cancelled: return .cancelled
        case .unsupportedFormat: return .unsupportedFormat
        case .outputFailed: return .outputFailed
        case .timedOut: return .timedOut
        case .seekFailed: return .seekFailed
        default: return .inputFailed
        }
    }
}
