// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 record-swipe.js: resident three-slot identity and commands.
// Movement, snapping and cancellation are owned by the native pager.
import Foundation

/// Presentation state only. Selection is supplied by MusicViewModel's real
/// queue commit. No preview calls advance(), random(), a service or a player.
struct MusicRecordTransition: Equatable, Sendable {
    enum Phase: Equatable, Sendable {
        case idle, dragging, awaiting, settling, returning
    }
    struct Slot: Equatable, Sendable, Identifiable {
        let id: Int
        var entry: MusicQueueEntry?
    }
    private(set) var slots = (0..<3).map { Slot(id: $0, entry: nil) }
    private(set) var center = 1
    private(set) var phase: Phase = .idle
    private(set) var token: UUID?
    private(set) var origin: MusicReferencePlayerFence?
    private(set) var direction: MusicDirection?
    private(set) var selectedID: String?
    var busy: Bool { phase != .idle && phase != .dragging }
    var hasCommand: Bool { phase == .awaiting || phase == .settling }

    func neighbor(_ direction: MusicDirection) -> Int {
        (center + (direction == .next ? 1 : 2)) % 3
    }
    mutating func synchronize(current: MusicQueueEntry?, previous: MusicQueueEntry?, next: MusicQueueEntry?) {
        guard phase == .idle else { return }
        slots[center].entry = current
        slots[neighbor(.previous)].entry = previous
        slots[neighbor(.next)].entry = next
    }
    mutating func begin(_ fence: MusicReferencePlayerFence) -> Bool {
        guard phase == .idle, fence.occurrence != nil else { return false }
        phase = .dragging; origin = fence
        return true
    }
    mutating func request(_ direction: MusicDirection, fence: MusicReferencePlayerFence) -> UUID? {
        guard phase == .idle || phase == .dragging,
              origin == nil || origin == fence, fence.occurrence != nil else { return nil }
        let token = UUID()
        self.token = token; self.direction = direction; origin = fence
        phase = .awaiting; selectedID = nil
        return token
    }
    /// Changes made by the admitted navigation are expected. Scope/settings
    /// changes still revoke it. A mere gesture requires the exact old fence.
    func accepts(_ fence: MusicReferencePlayerFence) -> Bool {
        guard let origin else { return true }
        if hasCommand {
            return origin.access == fence.access && origin.settingsRevision == fence.settingsRevision &&
                (phase == .awaiting || selectedID == fence.occurrence)
        }
        return origin == fence
    }
    mutating func selected(_ entry: MusicQueueEntry, token: UUID,
                           current: MusicReferencePlayerFence) -> Bool {
        guard self.token == token, phase == .awaiting, accepts(current),
              current.occurrence == entry.id, let direction else { return false }
        slots[neighbor(direction)].entry = entry
        selectedID = entry.id; phase = .settling
        return true
    }
    mutating func returnToOrigin() -> UUID? {
        guard phase != .idle, phase != .settling else { return nil }
        let token = UUID(); self.token = token
        phase = .returning; selectedID = nil
        return token
    }
    @discardableResult
    mutating func finish(_ token: UUID, currentID: String?) -> Bool {
        guard self.token == token else { return false }
        if phase == .settling, selectedID == currentID, let direction {
            center = neighbor(direction)
        } else if phase != .returning { return false }
        cancel()
        return true
    }
    mutating func cancel() {
        phase = .idle; token = nil; origin = nil; direction = nil; selectedID = nil
    }
}
