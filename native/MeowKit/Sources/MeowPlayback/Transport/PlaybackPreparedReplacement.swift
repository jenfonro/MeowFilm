#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import AVFoundation
import MeowContracts

/// A silent, independently cancellable item. It never owns the audio session or
/// emits playback facts; ownership changes only at the controller's commit.
@MainActor
public final class PlaybackPreparedReplacement {
    let resource: PlaybackResource
    let replacing: PlayAttempt
    let player = AVPlayer()
    var binding: NativePlaybackAsset?
    var route: HLSLoopbackRoute?
    var localMediaFetch = false
    var committed = false
    var synchronizationDeadline: ContinuousClock.Instant?
    private var failure: Error?
    let failures = PlaybackReplacementFailures()
    private var observer: NSKeyValueObservation?
    private var waiter: CheckedContinuation<Void, Error>?
    private var deadline: Task<Void, Never>?
    public var duration: Double? {
        guard let value = player.currentItem?.duration.seconds, value.isFinite, value > 0 else { return nil }
        return value
    }
    init(resource: PlaybackResource, replacing: PlayAttempt) {
        self.resource = resource; self.replacing = replacing
        player.automaticallyWaitsToMinimizeStalling = true; player.actionAtItemEnd = .pause
    }
    func prepare(budget: PlaybackNetworkBudget) async throws {
        let plan = try PlaybackNetworkPlan(resource: resource, options: .init())
        failures.receive = { [weak self] in self?.fail($0) }
        let failures = failures
        let onFailure: @Sendable (PlaybackNetworkError) -> Void = { error in
            Task { @MainActor in failures.receive?(error) }
        }
        if plan.route == .scopedHLS {
            let route = try HLSLoopbackRoute(plan: plan, budget: budget, onFailure: onFailure)
            self.route = route; localMediaFetch = true; player.allowsExternalPlayback = false
            try await wait {
                route.start { [weak self] result in
                    Task { @MainActor [weak self] in
                        guard let self, self.waiter != nil else { return }
                        switch result {
                        case .success(let url): self.binding = NativePlaybackAsset(hls: route, url: url); self.finish()
                        case .failure(let error): self.fail(error)
                        }
                    }
                }
            }
        } else { binding = try NativePlaybackAsset(plan: plan, budget: budget, onFailure: onFailure) }
        try Task.checkCancellation()
        guard let binding else { throw NativePlaybackError.preparationFailed }
        let item = AVPlayerItem(asset: binding.asset)
        player.replaceCurrentItem(with: item)
        try await wait {
            observer = item.observe(\.status, options: [.initial, .new]) { [weak self] item, _ in
                let status = item.status
                Task { @MainActor [weak self] in
                    if status == .readyToPlay { self?.finish() }
                    else if status == .failed { self?.fail(NativePlaybackError.preparationFailed) }
                }
            }
        }
        observer?.invalidate(); observer = nil
    }
    func seek(to position: Double, deadline: ContinuousClock.Instant) async throws {
        try await wait(until: deadline) {
            player.seek(to: CMTime(seconds: position, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] finished in
                Task { @MainActor [weak self] in
                    if finished { self?.finish() } else { self?.fail(NativePlaybackError.seekFailed) }
                }
            }
        }
    }
    private func wait(until end: ContinuousClock.Instant = .now.advanced(by: .seconds(15)), _ start: () -> Void) async throws {
        try Task.checkCancellation()
        if let failure { throw failure }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                waiter = continuation
                deadline = Task { [weak self] in
                    do { try await Task.sleep(until: end, clock: .continuous) } catch { return }
                    self?.fail(NativePlaybackError.seekTimedOut)
                }
                start()
            }
        } onCancel: { Task { @MainActor [weak self] in self?.fail(CancellationError()) } }
        try Task.checkCancellation()
    }
    private func finish() {
        deadline?.cancel(); deadline = nil
        let continuation = waiter; waiter = nil; continuation?.resume()
    }
    private func fail(_ error: Error) {
        failure = error; deadline?.cancel(); deadline = nil
        let continuation = waiter; waiter = nil; continuation?.resume(throwing: error)
    }
    public func cancel() {
        guard !committed else { return }
        fail(CancellationError()); observer?.invalidate(); observer = nil
        player.pause(); player.currentItem?.cancelPendingSeeks(); player.replaceCurrentItem(with: nil)
        binding?.invalidate(); binding = nil; route?.invalidate(); route = nil
    }
    isolated deinit { if !committed { cancel() } }
}
@MainActor
final class PlaybackReplacementFailures {
    var receive: (@MainActor (PlaybackNetworkError) -> Void)?
}
#endif
