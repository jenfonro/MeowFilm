#if os(iOS) || os(macOS)
import Foundation

/// Application-owned library synchronization, suspended with the iOS lifecycle.
/// b900c259 src/library.ts:51-60: immediate start + five-minute bounded rounds.
@MainActor public final class MusicAutomaticLibrarySync {
    public static let interval: TimeInterval = 5 * 60
    private let service: any MusicAutomaticLibrarySyncing
    private let refresh: @MainActor () async -> Void
    private let sleep: @Sendable (TimeInterval) async throws -> Void
    private var events: Task<Void, Never>?
    private var timer: Task<Void, Never>?
    private var run: Task<Void, Never>?
    private var draining: Task<Void, Never>?
    private var authorization: MusicQueueAuthorization?
    private var requestID: UUID?
    private var generation: UUID?
    private var epoch = UUID()
    private var active = false
    private var pendingMutation = false

    public init(service: any MusicAutomaticLibrarySyncing,
                onLibraryChanged: @escaping @MainActor () async -> Void,
                sleep: @escaping @Sendable (TimeInterval) async throws -> Void = {
                    try await Task.sleep(nanoseconds: UInt64($0 * 1_000_000_000))
                }) {
        self.service = service; refresh = onLibraryChanged; self.sleep = sleep
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
                    if self.run != nil { self.pendingMutation = true }
                    else { self.start(.localMutation) }
                case .refreshed: await self.refresh()
                }
            }
        }
    }

    public func stop() {
        active = false; epoch = UUID(); pendingMutation = false
        authorization?.revoke(); authorization = nil
        events?.cancel(); events = nil
        timer?.cancel(); timer = nil
        if let run { run.cancel(); draining = run }
        run = nil
        if let id = requestID {
            let service = service
            Task { await service.cancelLibrarySync(id: id) }
        }
        requestID = nil
    }

    private func current(_ own: UUID) -> Bool { active && epoch == own && !Task.isCancelled }
    private func startTimer(_ own: UUID) {
        let sleep = sleep
        timer = Task { [weak self] in
            while !Task.isCancelled {
                do { try await sleep(Self.interval) } catch { return }
                guard let self, self.current(own) else { return }
                // No catch-up burst and no retry loop if offline/busy.
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
    deinit {
        authorization?.revoke(); events?.cancel(); timer?.cancel(); run?.cancel(); draining?.cancel()
    }
}
#endif
