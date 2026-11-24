import Foundation

/// Pure phase timeout tracker. Headers, empty chunks and decoder reads never renew it.
struct NativeMusicAudioCacheDeadline: Sendable {
    private let started: ContinuousClock.Instant
    private var lastByte: ContinuousClock.Instant
    private var inspectionStarted: ContinuousClock.Instant?

    init() {
        let now = ContinuousClock.now
        started = now; lastByte = now
    }

    func remaining() -> Duration {
        let now = ContinuousClock.now
        let end: ContinuousClock.Instant
        if let inspectionStarted {
            end = min(inspectionStarted.advanced(by: .seconds(NativeMusicAudioCacheHTTPPolicy.inspectionMaximum)),
                      started.advanced(by: .seconds(NativeMusicAudioCacheHTTPPolicy.transferMaximum +
                                                  NativeMusicAudioCacheHTTPPolicy.inspectionMaximum)))
        } else {
            end = min(lastByte.advanced(by: .seconds(NativeMusicAudioCacheHTTPPolicy.transferIdle)),
                      started.advanced(by: .seconds(NativeMusicAudioCacheHTTPPolicy.transferMaximum)))
        }
        return now.duration(to: end)
    }

    mutating func received(_ count: Int) throws {
        let now = ContinuousClock.now
        guard count >= 0, inspectionStarted == nil, remaining() > .zero else {
            throw NativeMusicAudioCacheFailure.miss
        }
        if count > 0 { lastByte = max(lastByte, now) }
    }

    mutating func completedBody() throws {
        let now = ContinuousClock.now
        guard inspectionStarted == nil, remaining() > .zero else {
            throw NativeMusicAudioCacheFailure.miss
        }
        inspectionStarted = now
    }
}
