#if os(iOS) || os(macOS)
import Foundation
import MeowDebug

/// Application-owned library synchronization, suspended with the iOS lifecycle.
/// b900c259 src/library.ts:51-60: immediate start + five-minute bounded rounds.
@MainActor public final class MusicAutomaticLibrarySync {
    public static let interval: TimeInterval = 5 * 60
    public static let localMutationDebounce: TimeInterval = 1.5
    private let service: any MusicAutomaticLibrarySyncing
    private let refresh: @MainActor () async -> Void
    private nonisolated(unsafe) var events: Task<Void, Never>?
    private nonisolated(unsafe) var timer: Task<Void, Never>?
    private nonisolated(unsafe) var mutationDebounce: Task<Void, Never>?
    private nonisolated(unsafe) var run: Task<Void, Never>?
    private nonisolated(unsafe) var draining: Task<Void, Never>?
    private nonisolated(unsafe) var authorization: MusicQueueAuthorization?
    private var requestID: UUID?
    private var generation: UUID?
    private var epoch = UUID()
    private var active = false
    private var pendingMutation = false

    public init(service: any MusicAutomaticLibrarySyncing,
                onLibraryChanged: @escaping @MainActor () async -> Void) {
        self.service = service; refresh = onLibraryChanged
    }

    /// Call from meowfilm's application owner on scene/protection/account-generation
    /// changes. No visible settings page is required, and inactive pages do not
    /// create additional owners.
    public func activate(isAvailable: Bool, accessGeneration: UUID, isForeground: Bool) {
        let next = isAvailable && isForeground
        guard next != active || accessGeneration != generation else { return }
        stop()
        generation = accessGeneration
        guard next else { return }
        active = true
        let own = epoch, service = service, previous = draining
        events = Task { [weak self] in
            // A cancelled non-cooperative read must drain before a successor
            // round. Never authorize the successor with the old request.
            await previous?.value
            guard self?.current(own) == true else { return }
            self?.draining = nil
            let stream = await service.automaticLibrarySyncEvents()
            guard self?.current(own) == true else { return }
            self?.start(.foreground)
            self?.startTimer(own)
            for await event in stream {
                guard let self, self.current(own) else { return }
                switch event {
                case .localMutation:
                    self.scheduleLocalMutationDebounce(own)
                case .refreshed:
                    await self.refresh()
                    if self.current(own), self.timer == nil {
                        self.startTimer(own)
                    }
                }
            }
        }
    }

    public func stop() {
        active = false; epoch = UUID(); pendingMutation = false
        authorization?.revoke(); authorization = nil
        events?.cancel(); events = nil
        timer?.cancel(); timer = nil
        mutationDebounce?.cancel(); mutationDebounce = nil
        if let run { run.cancel(); draining = run }
        run = nil
        if let id = requestID {
            let service = service
            Task { await service.cancelLibrarySync(id: id) }
        }
        requestID = nil
    }

    private func current(_ own: UUID) -> Bool { active && epoch == own && !Task.isCancelled }
    private func scheduleLocalMutationDebounce(_ own: UUID) {
        mutationDebounce?.cancel()
        let delayNanos = UInt64(Self.localMutationDebounce * 1_000_000_000)
        mutationDebounce = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: delayNanos) } catch { return }
            guard let self, self.current(own) else { return }
            self.mutationDebounce = nil
            if self.run != nil { self.pendingMutation = true }
            else { self.start(.localMutation) }
        }
    }
    private func startTimer(_ own: UUID) {
        timer?.cancel()
        let intervalNanos = UInt64(Self.interval * 1_000_000_000)
        timer = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(nanoseconds: intervalNanos) } catch { return }
                guard let self, self.current(own) else { return }
                // No catch-up burst, no retry loop if offline/busy, and stop
                // polling while blocked or waiting on unresolved fences until
                // a .refreshed event wakes the loop again.
                guard self.run == nil else { continue }
                do {
                    let status = try await self.service.librarySyncState()
                    guard self.current(own) else { return }
                    if status.phase == .blocked || status.hasUnresolvedFences {
                        self.timer = nil
                        return
                    }
                } catch {
                    if !Task.isCancelled {
                        DebugRuntime.logError(.musicLibrary, sub: "autoSync", event: "timer.stateReadFailed", error: error)
                    }
                    continue
                }
                if self.run == nil { self.start(.interval) }
            }
        }
    }
    private func start(_ reason: MusicAutomaticLibrarySyncRequest.Reason) {
        guard active, run == nil else { return }
        let own = epoch, id = UUID(), permission = MusicQueueAuthorization(), service = service
        authorization = permission; requestID = id
        run = Task { [weak self] in
            do {
                _ = try await service.synchronizeAutomaticLibrary(
                    .init(id: id, reason: reason, authorization: permission))
            } catch {
                // The service retains status, local intents and dispatched
                // fences. A failure is NOT another trigger.
                if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicLibrary, sub: "autoSync", event: "round.failed", error: error)
                }
            }
            permission.revoke()
            guard let self, self.current(own), self.requestID == id else { return }
            self.run = nil; self.authorization = nil; self.requestID = nil
            // Only a NEW committed user mutation can request an immediate next
            // round; an old failed/cancelled command is never queued for replay.
            if self.pendingMutation {
                self.pendingMutation = false; self.start(.localMutation)
            }
        }
    }
    nonisolated deinit {
        authorization?.revoke(); events?.cancel(); timer?.cancel(); mutationDebounce?.cancel(); run?.cancel(); draining?.cancel()
    }
}
#endif
