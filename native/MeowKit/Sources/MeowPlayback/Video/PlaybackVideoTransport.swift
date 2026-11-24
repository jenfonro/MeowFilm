#if os(iOS) || os(macOS)
import Foundation
import CoreGraphics
import MeowContracts

@MainActor
final class PlaybackVideoTransport {
    var systemRelease: Task<NativePlaybackError?, Never>?
    let media: PlaybackVideoMediaLease
    let title: String
    let initialRate: Double?
    let showsNowPlaying: Bool
    let kernel: any PlaybackVideoDecoding
    var output: any PlaybackVideoOutput { kernel.output }
    private var input: PlaybackVideoInput?
    private var localClosing: Task<Void, Never>?
    private var closing: Task<Void, Error>?
    private(set) var attempt: PlayAttempt?
    var position: Double { kernel.position }
    var bufferedPosition: Double { kernel.bufferedPosition }
    var isBuffering: Bool { kernel.isBuffering }
    var videoSize: CGSize { kernel.videoSize }
    var duration: Double? { kernel.duration }
    var seekable: Bool { kernel.seekable }
    var tracks: [PlaybackVideoTrack] { kernel.tracks }
    var diagnostics: PlaybackVideoDiagnostics? { kernel.diagnostics }
    #if os(iOS)
    private var pictureInPicture: PlaybackVideoPictureInPicture?
    #endif

    init(media: PlaybackVideoMediaLease, input: PlaybackVideoInput, decoder: any PlaybackVideoDecoding,
         initialRate: Double? = nil, showsNowPlaying: Bool = true) {
        self.media = media; self.input = input; title = input.title
        self.initialRate = initialRate; self.showsNowPlaying = showsNowPlaying
        kernel = decoder
    }

    func prepare() async throws {
        guard let input else { throw PlaybackVideoError.staleAttempt }
        // Drop credential-bearing input immediately regardless of success or failure.
        self.input = nil
        let format: String?
        switch input.format { case .hls: format = "hls"; case .dash: format = "dash"; default: format = nil }
        do {
            try await kernel.prepare(url: input.url, format: format,
                clearKeys: input.decryption?.withKeyMaterial { $0 } ?? [:],
                position: input.startPosition, headers: input.requestContext.headers)
        } catch { throw PlaybackVideoLease.error(error) }
    }

    func align(to position: Double) async throws { try await kernel.align(to: position) }
    func renderFirstVideoFrame() async throws { try await kernel.renderFirstVideoFrame() }
    func validateForCommit() throws {
        do { try kernel.validateForCommit() }
        catch { throw PlaybackVideoLease.error(error) }
    }
    func commit(attempt: PlayAttempt, rate: Float, volume: Float) {
        self.attempt = attempt
        kernel.commit(rate: rate, volume: volume)
    }
    func setRate(_ rate: Float) { kernel.setRate(rate) }
    func setVolume(_ volume: Float) { output.volume = volume }
    func seek(to position: Double) async throws -> Double { try await kernel.seek(to: position) }
    func selectTrack(_ id: Int, kind: PlaybackVideoTrack.Kind) async throws {
        do { try await kernel.selectTrack(id, kind: kind) }
        catch { throw PlaybackVideoLease.error(error) }
    }
    func disableSubtitle() throws {
        do { try kernel.disableSubtitle() }
        catch { throw PlaybackVideoLease.error(error) }
    }

    func cancel() {
        input = nil
        output.onDisplayLayerChanged = nil
        kernel.cancel()
        #if os(iOS)
        pictureInPicture?.stop()
        #endif
    }

    /// Physical PiP and decoder drain only, without releasing the shared runtime media receipt.
    /// Used both for explicit pre-commit decoder fallback and for separating physical output
    /// admission from remote Node release ACK failures.
    func closeLocal() async {
        if localClosing == nil {
            cancel()
            let kernel = kernel
            #if os(iOS)
            let pictureInPicture = pictureInPicture
            self.pictureInPicture = nil
            #endif
            localClosing = Task {
                #if os(iOS)
                await pictureInPicture?.close()
                #endif
                await kernel.close()
            }
        }
        await localClosing!.value
    }

    func close() async throws {
        if closing == nil {
            cancel()
            let media = media
            closing = Task { [self] in
                await closeLocal()
                do { try await media.close() }
                catch { throw PlaybackVideoError.resourceReleaseFailed }
            }
        }
        try await closing!.value
    }

    #if os(iOS)
    var hasActivePictureInPicture: Bool { pictureInPicture?.isActive == true }
    var hasPictureInPictureOwner: Bool { pictureInPicture?.hasSystemOwner == true }
    var pictureInPictureState: PlaybackVideoPictureInPictureState { pictureInPicture?.state ?? .unavailable }
    func takeActivePictureInPicture() -> PlaybackVideoPictureInPicture? {
        guard let pictureInPicture, pictureInPicture.isActive else { return nil }
        self.pictureInPicture = nil
        return pictureInPicture
    }
    func adoptPictureInPicture(_ controller: PlaybackVideoPictureInPicture) {
        pictureInPicture = controller
    }
    func installPictureInPicture(lease: PlaybackVideoLease, controller: PlaybackController, attempt: PlayAttempt) {
        if let pictureInPicture {
            pictureInPicture.rebind(transport: self, lease: lease, controller: controller, attempt: attempt)
        } else {
            pictureInPicture = PlaybackVideoPictureInPicture(transport: self, lease: lease, controller: controller, attempt: attempt)
        }
        lease.installPictureInPictureState(pictureInPicture?.state ?? .unavailable,
                                           transport: self, attempt: attempt)
        output.onDisplayLayerChanged = { [weak self, weak lease, weak controller] in
            guard let self, let lease, let controller, controller.isCurrent(attempt) else { return }
            lease.surface.reapplyGravity()
            self.pictureInPicture?.rebind(transport: self, lease: lease, controller: controller, attempt: attempt)
        }
    }
    func startPictureInPicture(attempt: PlayAttempt) -> Bool {
        guard self.attempt == attempt else { return false }
        return pictureInPicture?.start() ?? false
    }
    func stopPictureInPicture() { pictureInPicture?.stop() }
    func updatePictureInPicture() { pictureInPicture?.invalidateState() }
    #else
    var hasActivePictureInPicture: Bool { false }
    var hasPictureInPictureOwner: Bool { false }
    var pictureInPictureState: PlaybackVideoPictureInPictureState { .unavailable }
    func installPictureInPicture(lease: PlaybackVideoLease, controller: PlaybackController, attempt: PlayAttempt) {
        lease.installPictureInPictureState(.unavailable, transport: self, attempt: attempt)
    }
    func updatePictureInPicture() {}
    #endif
}
#endif
