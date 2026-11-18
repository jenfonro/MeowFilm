import Combine
import Foundation
import MeowContracts
import MeowRuntime

public struct FilmPlaybackSegment: Equatable, Sendable {
    public enum Kind: String, Sendable { case intro, outro }
    public let kind: Kind
    public let start: Double
    public let end: Double
    public var title: String { kind == .intro ? "跳过片头" : "跳过片尾" }

    public init(kind: Kind, start: Double, end: Double) {
        self.kind = kind; self.start = start; self.end = end
    }
}

/// Borrows the film owner; chapter recognition cannot acquire or release media.
@MainActor
public final class FilmPlaybackChapterModel: ObservableObject {
    @Published public private(set) var segments: [FilmPlaybackSegment] = []
    @Published public private(set) var error: FilmPlaybackError?
    private struct Input: Equatable {
        let sessionID: UUID
        let attemptID: String
        let resourceID: String
        let chapters: [PlaybackVideoChapter]
    }
    private let transport: FilmPlaybackTransport
    private var input: Input?
    private var generation = 0
    private(set) var task: Task<Void, Never>?
    private var closed = false

    public init(transport: FilmPlaybackTransport) { self.transport = transport }
    deinit { task?.cancel() }

    public func update(sessionID: UUID?, attemptID: String?, resourceID: String?,
                       chapters: [PlaybackVideoChapter], enabled: Bool) {
        guard !closed else { return }
        guard enabled, let sessionID, let attemptID, let resourceID else {
            invalidate(); return
        }
        let next = Input(sessionID: sessionID, attemptID: attemptID, resourceID: resourceID, chapters: chapters)
        guard next != input else { return }
        invalidate()
        guard !attemptID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !resourceID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              chapters.allSatisfy({
                  $0.start.isFinite && $0.start >= 0 &&
                      $0.end.isFinite && $0.end > $0.start
              }) else {
            error = .init(code: "INVALID_REQUEST"); return
        }
        input = next
        guard !chapters.isEmpty else { return }
        let epoch = generation, transport = transport
        task = Task { [weak self] in
            do {
                let value = try await transport.call("session.command", parameters: .object([
                    "sessionId": .string(sessionID.uuidString), "action": .string("media.chapters"),
                    "attemptId": .string(attemptID), "resourceId": .string(resourceID),
                    "chapters": .array(chapters.map {
                        .object(["title": .string($0.title), "start": .number($0.start), "end": .number($0.end)])
                    }),
                ]))
                try Task.checkCancellation()
                let result = try Self.decode(value, chapters: chapters)
                guard let self, !self.closed, self.generation == epoch else { return }
                self.segments = result
                self.task = nil
            } catch {
                guard !Task.isCancelled, let self, !self.closed, self.generation == epoch else { return }
                if !(error is CancellationError) {
                    self.error = error as? FilmPlaybackError ?? .init(code: "UPSTREAM_ERROR")
                }
                self.task = nil
            }
        }
    }

    public func close() { closed = true; invalidate() }

    private func invalidate() {
        generation += 1
        task?.cancel(); task = nil
        input = nil; segments = []; error = nil
    }

    private static func decode(_ value: RuntimeValue, chapters: [PlaybackVideoChapter]) throws -> [FilmPlaybackSegment] {
        let fields = try value.filmRequiredObject()
        let rows = try fields.filmArray("segments")
        guard rows.count <= chapters.count else { throw FilmPlaybackError.invalidResponse }
        var result: [FilmPlaybackSegment] = []
        for row in rows {
            let fields = try row.filmRequiredObject()
            guard let kind = FilmPlaybackSegment.Kind(rawValue: try fields.filmString("kind")) else {
                throw FilmPlaybackError.invalidResponse
            }
            let start = try fields.filmNumber("start"), end = try fields.filmNumber("end")
            guard end > start, chapters.contains(where: { $0.start == start && $0.end == end }) else {
                throw FilmPlaybackError.invalidResponse
            }
            let segment = FilmPlaybackSegment(kind: kind, start: start, end: end)
            guard !result.contains(segment) else { throw FilmPlaybackError.invalidResponse }
            result.append(segment)
        }
        return result
    }
}
