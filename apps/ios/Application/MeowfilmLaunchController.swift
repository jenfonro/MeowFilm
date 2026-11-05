import Foundation
import Combine

/// The cold-launch fence is outside the business assembly. A foreground resume
/// cannot run an erase in a process that may still contain old writers/Node.
@MainActor
final class MeowfilmLaunchController: ObservableObject {
    enum Phase: Equatable {
        case loading, ready, restartRequired
        case failed(MeowfilmUserDataReset.Failure)
    }
    @Published private(set) var phase: Phase = .loading
    private let factory: @Sendable () throws -> MeowfilmUserDataReset
    private var reset: MeowfilmUserDataReset?
    private var startup: Task<Void, Never>?
    private var request: Task<Void, Error>?

    init(factory: @escaping @Sendable () throws -> MeowfilmUserDataReset = { try MeowfilmUserDataReset.live() }) {
        self.factory = factory
    }

    func start() {
        guard startup == nil, phase != .ready, phase != .restartRequired else { return }
        phase = .loading
        let factory = factory
        startup = Task {
            do {
                let reset = try await Task.detached(priority: .userInitiated) {
                    let reset = try factory()
                    try reset.completePendingReset()
                    return reset
                }.value
                self.reset = reset
                phase = .ready
            } catch {
                phase = .failed(error as? MeowfilmUserDataReset.Failure ?? .files)
            }
            startup = nil
        }
    }

    func requestReset(stop: @escaping @MainActor () -> Void) async throws {
        if let request { return try await request.value }
        guard phase == .ready, let reset else { throw MeowfilmUserDataReset.Failure.request }
        let task = Task {
            try await Task.detached(priority: .userInitiated) { try reset.request() }.value
            stop()
            phase = .restartRequired
        }
        request = task
        do { try await task.value }
        catch { request = nil; throw error }
    }
}
