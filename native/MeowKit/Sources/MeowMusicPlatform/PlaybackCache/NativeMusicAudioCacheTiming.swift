import Foundation

/// Monotonic time and suspension are injected together for deterministic slow
/// transport/decode regressions; wall-clock file TTLs have a different owner.
struct NativeMusicAudioCacheTiming: Sendable {
    var now: @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    var sleep: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
}

/// Pure phase policy. Headers, empty chunks and decoder reads never renew it.
struct NativeMusicAudioCacheDeadline: Sendable {
    private let started: ContinuousClock.Instant
    private var lastByte: ContinuousClock.Instant
    private var inspectionStarted: ContinuousClock.Instant?

    init(now: ContinuousClock.Instant) { started = now; lastByte = now }

    func remaining(at now: ContinuousClock.Instant) -> Duration {
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

    mutating func received(_ count: Int, at now: ContinuousClock.Instant) throws {
        guard count >= 0, inspectionStarted == nil, remaining(at: now) > .zero else {
            throw NativeMusicAudioCacheFailure.miss
        }
        if count > 0 { lastByte = max(lastByte, now) }
    }

    mutating func completedBody(at now: ContinuousClock.Instant) throws {
        guard inspectionStarted == nil, remaining(at: now) > .zero else {
            throw NativeMusicAudioCacheFailure.miss
        }
        inspectionStarted = now
    }
}
