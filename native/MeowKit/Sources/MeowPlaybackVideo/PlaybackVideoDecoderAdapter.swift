import Foundation
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowVideoKernel

/// Explicit App composition, outside the music/shared dependency closure.
@MainActor
public func makePlaybackVideoDecoder() throws -> any PlaybackVideoDecoding {
    PlaybackVideoDecoderAdapter(session: try VideoSession())
}

@MainActor
public func makePlaybackVideoDecoder(engine: PlaybackVideoEngine) throws -> any PlaybackVideoDecoding {
    DebugRuntime.log(.filmPlayback, sub: "video.decoder", event: "create", level: .debug, data: .object([
        "engine": .string(String(describing: engine))
    ]))
    switch engine {
    case .ksPlayer: return try makePlaybackVideoDecoder()
    case .aether:
        return PlaybackVideoCandidateDecoder(decoder: try AetherPlaybackVideoDecoderAdapter(),
                                             fallback: makePlaybackVideoDecoder)
    }
}

extension VideoOutput: PlaybackVideoOutput {}

@MainActor
private final class PlaybackVideoDecoderAdapter: PlaybackVideoDecoding {
    private let session: VideoSession
    var output: any PlaybackVideoOutput { session.output }
    var position: Double { session.position }
    var duration: Double? { session.info?.duration }
    var chapters: [PlaybackVideoChapter] {
        session.chapters.map { PlaybackVideoChapter(title: $0.title, start: $0.start, end: $0.end) }
    }
    var seekable: Bool { session.seekable }
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)? {
        didSet { session.onProgress = onProgress }
    }
    var onTransportRate: (@MainActor (Float) -> Void)? {
        didSet { session.onTransportRate = onTransportRate }
    }
    var onEnd: (@MainActor () -> Void)? {
        didSet { session.onEnd = onEnd }
    }

    init(session: VideoSession) {
        self.session = session
        session.onFailure = { [weak self] in self?.onFailure?(Self.error($0)) }
    }
    func prepare(url: URL, format: String?, clearKeys: [String: Data],
                 position: Double, headers: [String: String]) async throws {
        do { try await session.prepare(url: url, format: format, clearKeys: clearKeys,
                                       position: position, headers: headers) }
        catch { throw Self.error(error) }
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
        guard let error = error as? VideoKernelFailure else { return .inputFailed }
        switch error {
        case .cancelled, .interrupted: return .cancelled
        case .openFailed, .transportFailed: return .inputFailed
        case .formatFailed: return .unsupportedFormat
        case .decodeFailed: return .decodingFailed
        case .seekFailed: return .seekFailed
        case .outputFailed: return .outputFailed
        case .timedOut: return .timedOut
        case .decryptionFailed: return .decryptionFailed
        }
    }
}
