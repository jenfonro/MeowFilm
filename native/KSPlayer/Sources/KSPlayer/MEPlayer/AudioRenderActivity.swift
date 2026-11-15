import Foundation

/// Tracks real native render callbacks while an output is being stopped.
final class AudioRenderActivity {
    private let condition = NSCondition()
    private var active = 0
    private var closed = false

    var isClosed: Bool {
        condition.lock()
        defer { condition.unlock() }
        return closed
    }

    func begin() -> Bool {
        condition.lock()
        defer { condition.unlock() }
        guard !closed else { return false }
        active += 1
        return true
    }

    func end() {
        condition.lock()
        active -= 1
        condition.broadcast()
        condition.unlock()
    }

    func close() {
        condition.lock()
        closed = true
        condition.unlock()
    }

    func whenDrained(_ completion: @escaping () -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            self.condition.lock()
            while self.active != 0 {
                self.condition.wait()
            }
            self.condition.unlock()
            DispatchQueue.main.async(execute: completion)
        }
    }
}
