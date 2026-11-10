import Foundation
@preconcurrency import AVFoundation

/// Renderer requests execute on this serial non-UI queue, including PiP supply.
/// Each request is stopped when its current sample is consumed: a ready renderer
/// with an empty source must not turn into an unbounded callback spin.
final class VideoRendererFeed: @unchecked Sendable {
    private final class Request: @unchecked Sendable {
        // Accessed only on the feed queue, including cancellation-before-enqueue.
        var cancelled = false
        var continuation: CheckedContinuation<Void, Error>?
        func finish(_ result: Result<Void, Error>) {
            let continuation = continuation; self.continuation = nil
            continuation?.resume(with: result)
        }
    }
    let video: AVSampleBufferVideoRenderer
    let audio: AVSampleBufferAudioRenderer
    private let queue = DispatchQueue(label: "meow.video.render", qos: .userInitiated)
    private var videoPending: Request?
    private var audioPending: Request?
    private var revision = UUID()

    init(video: AVSampleBufferVideoRenderer, audio: AVSampleBufferAudioRenderer) {
        self.video = video; self.audio = audio
    }

    func enqueue(_ sample: VideoSample) async throws {
        let request = Request()
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                queue.async {
                    request.continuation = continuation
                    guard !request.cancelled else {
                        request.finish(.failure(VideoKernelFailure.cancelled)); return
                    }
                    let renderer: any AVQueuedSampleBufferRendering = sample.kind == .video ? self.video : self.audio
                    guard !(sample.kind == .video ? self.video.status == .failed : self.audio.status == .failed),
                          (sample.kind == .video ? self.videoPending : self.audioPending) == nil else {
                        request.finish(.failure(VideoKernelFailure.outputFailed)); return
                    }
                    let token = self.revision
                    if sample.kind == .video { self.videoPending = request }
                    else { self.audioPending = request }
                    renderer.requestMediaDataWhenReady(on: self.queue) {
                        guard token == self.revision, !request.cancelled, request.continuation != nil else { return }
                        let failed = sample.kind == .video ? self.video.status == .failed : self.audio.status == .failed
                        guard renderer.isReadyForMoreMediaData || failed else { return }
                        renderer.stopRequestingMediaData()
                        if sample.kind == .video { self.videoPending = nil }
                        else { self.audioPending = nil }
                        if failed { request.finish(.failure(VideoKernelFailure.outputFailed)) }
                        else { renderer.enqueue(sample.buffer); request.finish(.success(())) }
                    }
                }
            }
        } onCancel: {
            queue.async {
                request.cancelled = true
                let renderer: any AVQueuedSampleBufferRendering = sample.kind == .video ? self.video : self.audio
                let pending = sample.kind == .video ? self.videoPending : self.audioPending
                if pending === request {
                    renderer.stopRequestingMediaData()
                    if sample.kind == .video { self.videoPending = nil } else { self.audioPending = nil }
                }
                request.finish(.failure(VideoKernelFailure.cancelled))
            }
        }
    }

    func cancelRequests() {
        queue.async { self.stopRequests() }
    }

    private func stopRequests() {
        revision = UUID()
        video.stopRequestingMediaData(); audio.stopRequestingMediaData()
        let video = videoPending; videoPending = nil
        let audio = audioPending; audioPending = nil
        video?.finish(.failure(VideoKernelFailure.cancelled))
        audio?.finish(.failure(VideoKernelFailure.cancelled))
    }

    /// The barrier revokes every old request before cancelling its waiters.
    /// Video flush's callback is the actual renderer completion, not a delay.
    func stopAndFlush(removingImage: Bool) async {
        await withCheckedContinuation { continuation in
            queue.async {
                self.stopRequests()
                self.audio.flush()
                self.video.flush(removingDisplayedImage: removingImage) { continuation.resume() }
            }
        }
    }
}
