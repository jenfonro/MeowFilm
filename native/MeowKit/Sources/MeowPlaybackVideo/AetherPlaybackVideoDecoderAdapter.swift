import Foundation
import CoreGraphics
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
    var bufferedPosition: Double { session.bufferedPosition }
    var isBuffering: Bool { session.isBuffering }
    var videoSize: CGSize { session.videoSize }
    var duration: Double? { session.duration }
    var seekable: Bool { session.seekable }
    var chapters: [PlaybackVideoChapter] {
        session.chapters.map { PlaybackVideoChapter(title: $0.title, start: $0.start, end: $0.end) }
    }
    var tracks: [PlaybackVideoTrack] {
        let audio = session.audioTracks.map {
            PlaybackVideoTrack(id: $0.id, kind: .audio, name: $0.name, codec: $0.codec,
                               language: $0.language, isEnabled: $0.isEnabled)
        }
        let sub = session.subtitleTracks.map {
            PlaybackVideoTrack(id: $0.id, kind: .subtitle, name: $0.name, codec: $0.codec,
                               language: $0.language, isEnabled: $0.isEnabled)
        }
        return audio + sub
    }
    var diagnostics: PlaybackVideoDiagnostics? {
        let d = session.diagnostics
        let isDV = d.dolbyVisionProfile != nil
        let isHDR = isDV || d.videoFormat.lowercased().contains("hdr") || d.videoFormat.lowercased().contains("hlg")
        return PlaybackVideoDiagnostics(
            hardwareDecoding: d.hardwareDecoding,
            isHDR: isHDR,
            dolbyVision: isDV,
            dolbyVisionProfile: d.dolbyVisionProfile,
            videoFormat: d.videoFormat,
            bufferedSeconds: d.bufferedSeconds
        )
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
    func selectTrack(_ id: Int, kind: PlaybackVideoTrack.Kind) async throws {
        do {
            switch kind {
            case .audio:
                try session.selectAudioTrack(id: id)
            case .subtitle:
                try session.selectSubtitleTrack(id: id)
            case .video:
                throw PlaybackVideoError.unsupportedFormat
            }
        } catch { throw Self.error(error) }
    }
    func disableSubtitle() throws {
        do { try session.selectSubtitleTrack(id: nil) }
        catch { throw Self.error(error) }
    }
    func cancel() { session.cancel() }
    func close() async { await session.close() }

    private static func error(_ error: Error) -> PlaybackVideoError {
        if error is CancellationError { return .cancelled }
        if let videoError = error as? PlaybackVideoError { return videoError }
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
