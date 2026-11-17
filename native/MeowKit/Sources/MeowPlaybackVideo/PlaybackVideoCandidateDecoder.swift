import Foundation
import AVFoundation
import MeowContracts
import MeowPlayback
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// A single uncommitted media candidate. Replacing an unsupported decoder does
/// not release or reacquire the runtime's media receipt.
@MainActor
final class PlaybackVideoCandidateDecoder: PlaybackVideoDecoding, PlaybackVideoOutput {
    private var decoder: any PlaybackVideoDecoding
    private var fallback: PlaybackVideoDecoderFactory?
    private var input: Input?
    private var cancelled = false
    private var committed = false
    private var gravity: AVLayerVideoGravity = .resizeAspect
    private var decoderGeneration = UUID()
    private struct Input {
        let url: URL
        let format: String?
        let clearKeys: [String: Data]
        var position: Double
        let headers: [String: String]
    }

    #if os(iOS)
    let presentationView = UIView()
    #else
    let presentationView = NSView()
    #endif
    var output: any PlaybackVideoOutput { self }
    var engine: PlaybackVideoEngine { decoder.engine }
    var chapters: [PlaybackVideoChapter] { decoder.chapters }
    var position: Double { decoder.position }
    var duration: Double? { decoder.duration }
    var seekable: Bool { !cancelled && decoder.seekable }
    var volume: Float {
        get { decoder.output.volume }
        set { decoder.output.volume = newValue }
    }
    var rate: Float { decoder.output.rate }
    var displayLayer: AVSampleBufferDisplayLayer? { decoder.output.displayLayer }
    var playerLayer: AVPlayerLayer? { decoder.output.playerLayer }
    var onDisplayLayerChanged: (() -> Void)?
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)?
    var onTransportRate: (@MainActor (Float) -> Void)?
    var onPlaybackIntent: (@MainActor (Bool) -> Void)?
    var onEnd: (@MainActor () -> Void)?

    init(decoder: any PlaybackVideoDecoding, fallback: @escaping PlaybackVideoDecoderFactory) {
        self.decoder = decoder; self.fallback = fallback
        install()
    }

    private func install() {
        let generation = UUID()
        decoderGeneration = generation
        presentationView.subviews.forEach { $0.removeFromSuperview() }
        let view = decoder.output.presentationView
        view.frame = presentationView.bounds
        #if os(iOS)
        view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        #else
        view.autoresizingMask = [.width, .height]
        #endif
        presentationView.addSubview(view)
        decoder.output.setVideoGravity(gravity)
        decoder.output.onDisplayLayerChanged = { [weak self] in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.decoder.output.setVideoGravity(self.gravity)
            self.onDisplayLayerChanged?()
        }
        decoder.onFailure = { [weak self] error in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.onFailure?(error)
        }
        decoder.onProgress = { [weak self] position in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.onProgress?(position)
        }
        decoder.onTransportRate = { [weak self] rate in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.onTransportRate?(rate)
        }
        decoder.onPlaybackIntent = { [weak self] playing in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.onPlaybackIntent?(playing)
        }
        decoder.onEnd = { [weak self] in
            guard let self, !self.cancelled, self.decoderGeneration == generation else { return }
            self.onEnd?()
        }
    }

    func prepare(url: URL, format: String?, clearKeys: [String: Data],
                 position: Double, headers: [String: String]) async throws {
        try check()
        input = Input(url: url, format: format, clearKeys: clearKeys, position: position, headers: headers)
        do { try await prepareCurrent() }
        catch { try await replaceUnsupported(error) }
    }

    private func prepareCurrent() async throws {
        try check()
        guard let input else { throw PlaybackVideoError.staleAttempt }
        try await decoder.prepare(url: input.url, format: input.format, clearKeys: input.clearKeys,
                                  position: input.position, headers: input.headers)
        try check()
    }

    private func replaceUnsupported(_ error: Error) async throws {
        try check()
        guard !committed, error as? PlaybackVideoError == .unsupportedFormat,
              let fallback else { throw error }
        self.fallback = nil
        decoderGeneration = UUID()
        decoder.cancel()
        await decoder.close()
        try check()
        decoder = try fallback()
        install()
        try await prepareCurrent()
    }

    func align(to position: Double) async throws {
        try check()
        input?.position = position
        do { try await decoder.align(to: position) }
        catch { try await replaceUnsupported(error) }
        try check()
    }

    func renderFirstVideoFrame() async throws {
        try check()
        do { try await decoder.renderFirstVideoFrame() }
        catch {
            try await replaceUnsupported(error)
            try await decoder.renderFirstVideoFrame()
        }
        try check()
    }

    func validateForCommit() throws { try check(); try decoder.validateForCommit() }
    func commit(rate: Float, volume: Float) {
        committed = true; input = nil; fallback = nil
        decoder.commit(rate: rate, volume: volume)
    }
    func setRate(_ rate: Float) { decoder.setRate(rate) }
    func seek(to position: Double) async throws -> Double { try await decoder.seek(to: position) }
    func setVideoGravity(_ gravity: AVLayerVideoGravity) {
        self.gravity = gravity; decoder.output.setVideoGravity(gravity)
    }
    func setPictureInPictureActive(_ active: Bool) { decoder.output.setPictureInPictureActive(active) }
    func cancel() { cancelled = true; input = nil; fallback = nil; decoder.cancel() }
    func close() async { cancel(); await decoder.close(); presentationView.removeFromSuperview() }
    private func check() throws {
        try Task.checkCancellation()
        if cancelled { throw PlaybackVideoError.cancelled }
    }
}
