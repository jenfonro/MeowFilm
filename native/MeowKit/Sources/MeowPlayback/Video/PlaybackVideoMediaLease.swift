#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

/// One runtime media receipt. Construction only retains an already-resolved
/// input; it never opens a decoder, reads media or acquires system ownership.
@MainActor
public final class PlaybackVideoMediaLease: CustomStringConvertible, CustomDebugStringConvertible {
    public let attemptID: String
    public let resourceID: String
    private(set) var input: PlaybackVideoInput?
    private let release: @MainActor () async throws -> Void
    private var releaseTask: Task<Void, Error>?

    public init(attemptID: String, input: PlaybackVideoInput,
                release: @escaping @MainActor () async throws -> Void) throws {
        guard !attemptID.isEmpty else { throw ContractError.invalidIdentifier }
        self.attemptID = attemptID; resourceID = input.resourceID
        self.input = input; self.release = release
    }

    func takeInput() -> PlaybackVideoInput? {
        let input = input; self.input = nil
        return input
    }

    /// Coalesces concurrent releases; a failed release remains a failed receipt,
    /// never an empty success. The native owner calls this after decoder drain.
    public func close() async throws {
        if releaseTask == nil {
            input = nil
            let release = release
            releaseTask = Task { try await release() }
        }
        try await releaseTask!.value
    }

    public nonisolated var description: String { "PlaybackVideoMediaLease(redacted)" }
    public nonisolated var debugDescription: String { description }
}
#endif
