import Foundation
import CoreGraphics
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowVideoKernel
import MeowAetherKernel

/// Explicit App composition, outside the music/shared dependency closure.
@MainActor
public func makePlaybackVideoDecoder(engine: PlaybackVideoEngine = .native) throws -> any PlaybackVideoDecoding {
    installVideoKernelDebugLogsIfActive()
    DebugRuntime.log(.filmPlayback, sub: "video.decoder", event: "create", level: .debug, data: .object([
        "engine": .string(engine.rawValue)
    ]))
    switch engine {
    case .native:
        return PlaybackVideoDecoderAdapter(session: try VideoSession())
    case .aether:
        return try AetherPlaybackVideoDecoderAdapter()
    }
}

@MainActor
private var installedVideoDebugLogHandlers = false

@MainActor
private func installVideoKernelDebugLogsIfActive() {
    guard !installedVideoDebugLogHandlers, DebugRuntime.current != nil else { return }
    installedVideoDebugLogHandlers = true
    VideoSession.setLogHandler { level, message in
        let debugLevel: DebugLogLevel
        if level <= 16 { debugLevel = .error }
        else if level <= 24 { debugLevel = .warn }
        else { debugLevel = .debug }
        DebugRuntime.log(.filmPlayback, sub: "native.ffmpeg", event: "log", level: debugLevel, data: .object([
            "avLevel": .int(Int(level)),
            "message": .string(message)
        ]))
    }
    AetherVideoSession.setLogHandler { category, message in
        DebugRuntime.log(.filmPlayback, sub: "aether.\(category)", event: "log", level: .debug, data: .object([
            "message": .string(message)
        ]))
    }
}

extension VideoOutput: PlaybackVideoOutput {}

@MainActor
private final class PlaybackVideoDecoderAdapter: PlaybackVideoDecoding {
    private let session: VideoSession
    var engine: PlaybackVideoEngine { .native }
    var output: any PlaybackVideoOutput { session.output }
    var position: Double { session.position }
    var bufferedPosition: Double { session.bufferedPosition }
    var isBuffering: Bool { session.isBuffering }
    var videoSize: CGSize { session.videoSize }
    var duration: Double? { session.info?.duration }
    var chapters: [PlaybackVideoChapter] {
        session.chapters.map { PlaybackVideoChapter(title: $0.title, start: $0.start, end: $0.end) }
    }
    var tracks: [PlaybackVideoTrack] {
        session.tracks.map {
            let kind: PlaybackVideoTrack.Kind
            switch $0.kind {
            case .video: kind = .video
            case .audio: kind = .audio
            case .subtitle: kind = .subtitle
            }
            return PlaybackVideoTrack(id: Int($0.id), kind: kind, name: $0.name, codec: $0.codec,
                                      language: $0.language, isEnabled: $0.isEnabled)
        }
    }
    var seekable: Bool { session.seekable }
    var diagnostics: PlaybackVideoDiagnostics? {
        guard let d = session.diagnostics else { return nil }
        return PlaybackVideoDiagnostics(
            droppedFrames: d.droppedFrames,
            avSyncDiffSeconds: d.avSyncDiffSeconds,
            videoQueuePackets: d.videoQueuePackets,
            audioQueuePackets: d.audioQueuePackets,
            subtitleQueuePackets: d.subtitleQueuePackets,
            videoFrameQueue: d.videoFrameQueue,
            hardwareDecoding: d.hardwareDecoding,
            hwDecodeFallback: d.hwDecodeFallback,
            isHDR: d.isHDR,
            dolbyVision: d.dolbyVision,
            dolbyVisionProfile: session.info?.dolbyVisionProfile.map(Int.init),
            videoFormat: session.info?.codec,
            bufferedSeconds: d.bufferedSeconds,
            audioUnderruns: d.audioUnderruns,
            submittedPictures: d.submittedPictures,
            lastErrorCode: d.lastErrorCode
        )
    }
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)? {
        didSet { session.onProgress = onProgress }
    }
    var onTransportRate: (@MainActor (Float) -> Void)? {
        didSet { session.onTransportRate = onTransportRate }
    }
    var onPlaybackIntent: (@MainActor (Bool) -> Void)?
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
    func selectTrack(_ id: Int, kind: PlaybackVideoTrack.Kind) async throws {
        guard let trackID = Int32(exactly: id) else { throw PlaybackVideoError.unsupportedFormat }
        let mapped: VideoTrack.Kind
        switch kind {
        case .video: mapped = .video
        case .audio: mapped = .audio
        case .subtitle: mapped = .subtitle
        }
        do { try await session.selectTrack(trackID, kind: mapped) }
        catch { throw Self.error(error) }
    }
    func disableSubtitle() throws {
        session.disableSubtitle()
    }
    func cancel() { session.cancel() }
    func close() async { await session.close() }

    private static func error(_ error: Error) -> PlaybackVideoError {
        if error is CancellationError { return .cancelled }
        guard let error = error as? VideoKernelFailure else { return .inputFailed }
        switch error {
        case .cancelled: return .cancelled
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
