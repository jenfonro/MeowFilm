#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowVideoKernel

@MainActor
final class PlaybackVideoTransport {
    var systemRelease: Task<NativePlaybackError?, Never>?
    let media: PlaybackVideoMediaLease
    let title: String
    let kernel: VideoSession
    var output: VideoSampleOutput { kernel.output }
    private var input: PlaybackVideoInput?
    private var closing: Task<Void, Error>?
    private(set) var attempt: PlayAttempt?
    var position: Double { kernel.position }
    var duration: Double? { kernel.info?.duration }
    private var pictureInPicture: (any PlaybackVideoPictureInPictureControlling)?

    init(media: PlaybackVideoMediaLease, input: PlaybackVideoInput) throws {
        self.media = media; self.input = input; title = input.title
        kernel = try VideoSession()
    }

    func prepare() async throws {
        guard let input else { throw PlaybackVideoError.staleAttempt }
        let format: String?
        switch input.format { case .hls: format = "hls"; case .dash: format = "dash"; default: format = nil }
        do {
            try await kernel.prepare(url: input.url, format: format,
                clearKeys: input.decryption?.withKeyMaterial { $0 } ?? [:],
                position: input.startPosition)
            self.input = nil
        } catch { throw PlaybackVideoLease.error(error) }
    }

    func align(to position: Double) async throws { try await kernel.align(to: position) }
    func renderFirstVideoFrame() async throws { try await kernel.renderFirstVideoFrame() }
    func commit(attempt: PlayAttempt, rate: Float, volume: Float) {
        self.attempt = attempt
        kernel.commit(rate: rate, volume: volume)
    }
    func setRate(_ rate: Float) { kernel.setRate(rate) }
    func setVolume(_ volume: Float) { output.volume = volume }
    func seek(to position: Double) async throws -> Double { try await kernel.seek(to: position) }
    func cancel() {
        input = nil; kernel.cancel()
        pictureInPicture?.stop()
    }
    func close() async throws {
        if closing == nil {
            cancel()
            let kernel = kernel, media = media
            let pictureInPicture = pictureInPicture
            self.pictureInPicture = nil
            closing = Task {
                await pictureInPicture?.close()
                await kernel.close()
                do { try await media.close() }
                catch { throw PlaybackVideoError.resourceReleaseFailed }
            }
        }
        try await closing!.value
    }
    var hasActivePictureInPicture: Bool { pictureInPicture?.isActive == true }
    var hasPictureInPictureOwner: Bool { pictureInPicture?.hasSystemOwner == true }
    var pictureInPictureState: PlaybackVideoPictureInPictureState { pictureInPicture?.state ?? .unavailable }
    func takeActivePictureInPicture() -> (any PlaybackVideoPictureInPictureControlling)? {
        guard let pictureInPicture, pictureInPicture.isActive else { return nil }
        self.pictureInPicture = nil
        return pictureInPicture
    }
    func adoptPictureInPicture(_ controller: any PlaybackVideoPictureInPictureControlling) {
        pictureInPicture = controller
    }
    func installPictureInPicture(lease: PlaybackVideoLease, controller: PlaybackController, attempt: PlayAttempt) {
        if let pictureInPicture {
            pictureInPicture.rebind(transport: self, lease: lease, controller: controller, attempt: attempt)
        } else {
            #if os(iOS)
            pictureInPicture = PlaybackVideoPictureInPicture(transport: self, lease: lease, controller: controller, attempt: attempt)
            #endif
        }
        lease.installPictureInPictureState(pictureInPicture?.state ?? .unavailable,
                                           transport: self, attempt: attempt)
    }
    func startPictureInPicture(attempt: PlayAttempt) -> Bool {
        guard self.attempt == attempt else { return false }
        return pictureInPicture?.start() ?? false
    }
    func stopPictureInPicture() { pictureInPicture?.stop() }
    func updatePictureInPicture() { pictureInPicture?.invalidateState() }
}
#endif
