import Foundation

/// Bounded producer/consumer queue; suspension is real backpressure, not a
/// dropped frame or periodic polling. One oversized frame may occupy an empty
/// queue, so the memory budget is not a hidden resolution admission limit.
@MainActor
final class VideoFrameBuffer {
    private let maximumBytes: Int
    private let maximumCount: Int
    private var samples: [VideoSample] = []
    private var bytes = 0
    private var finished = false
    private var failure: Error?
    private var reader: CheckedContinuation<VideoSample?, Error>?
    private var writer: CheckedContinuation<Void, Error>?

    init(maximumBytes: Int, maximumCount: Int) {
        self.maximumBytes = maximumBytes; self.maximumCount = maximumCount
    }

    func push(_ sample: VideoSample) async throws {
        while !finished, reader == nil,
              samples.count >= maximumCount || (!samples.isEmpty && bytes + sample.byteCount > maximumBytes) {
            try await withCheckedThrowingContinuation { writer = $0 }
        }
        try Task.checkCancellation()
        guard !finished else { throw failure ?? VideoKernelFailure.cancelled }
        if let reader {
            self.reader = nil; reader.resume(returning: sample)
        } else {
            samples.append(sample); bytes += sample.byteCount
        }
    }

    func pop() async throws -> VideoSample? {
        try Task.checkCancellation()
        if !samples.isEmpty {
            let sample = samples.removeFirst(); bytes -= sample.byteCount
            let writer = writer; self.writer = nil; writer?.resume()
            return sample
        }
        if let failure { throw failure }
        if finished { return nil }
        return try await withCheckedThrowingContinuation { reader = $0 }
    }

    func finish(_ error: Error? = nil) {
        finished = true; failure = error
        if error != nil { samples.removeAll(); bytes = 0 }
        let reader = reader; self.reader = nil
        let writer = writer; self.writer = nil
        if let error { reader?.resume(throwing: error); writer?.resume(throwing: error) }
        else { reader?.resume(returning: nil); writer?.resume(throwing: VideoKernelFailure.cancelled) }
    }
}
