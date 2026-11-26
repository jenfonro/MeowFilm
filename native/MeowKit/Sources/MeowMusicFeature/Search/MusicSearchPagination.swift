// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

/// Shared ordered page dispatcher for songs and playlists. Successful prefixes
/// commit immediately; a failed range stays current, and speculative successes
/// remain reusable in the caller's cache. Completions are accepted in page order.
@MainActor enum MusicSearchRoundReader {
    private struct Completion<Value: Sendable>: Sendable {
        let request: MusicSearchPagination.Request
        let result: Result<Value, MusicServiceError>
    }
    static func read<Value: Sendable>(platform: MusicPublicBrowsePlatform, start: Int,
        pages: Int, concurrency: Int,
        load: @escaping @MainActor @Sendable (MusicSearchPagination.Request) async throws -> Value,
        accept: @escaping @MainActor (MusicSearchPagination.Request, Value) throws -> Int?) async throws {
        guard start > 0, pages >= 0, concurrency >= 1 else { throw MusicServiceError.invalidRequest }
        let size = MusicSearchPagination.pageSize(platform)
        var nextToStart = start, started = 0, inFlight = 0, exhausted = false
        var nextToAccept = start, accepted = 0
        var completed: [Int: Result<Value, MusicServiceError>] = [:]
        var failed: MusicServiceError?, halted = false
        try Task.checkCancellation()
        await withTaskGroup(of: Completion<Value>.self) { group in
            while true {
                while !halted, !exhausted, inFlight < concurrency, started < pages {
                    let request = MusicSearchPagination.Request(page: nextToStart, limit: size)
                    let (next, overflow) = nextToStart.addingReportingOverflow(1)
                    if overflow { exhausted = true } else { nextToStart = next }
                    started += 1; inFlight += 1
                    group.addTask { @MainActor in
                        do { return .init(request: request, result: .success(try await load(request))) }
                        catch { return .init(request: request, result: .failure(.sanitized(error))) }
                    }
                }
                guard inFlight > 0 else { break }
                guard let value = await group.next() else { break }
                inFlight -= 1
                completed[value.request.page] = value.result
                while !halted, accepted < pages, let result = completed.removeValue(forKey: nextToAccept) {
                    let request = MusicSearchPagination.Request(page: nextToAccept, limit: size)
                    switch result {
                    case .failure(let error):
                        failed = error; halted = true; group.cancelAll()
                    case .success(let value):
                        do {
                            let cursor = try accept(request, value)
                            accepted += 1
                            if cursor == request.nextPage {
                                let (next, overflow) = nextToAccept.addingReportingOverflow(1)
                                if overflow { exhausted = true } else { nextToAccept = next }
                            } else {
                                // A real end/gap supersedes speculative adjacent pages.
                                halted = true; group.cancelAll()
                            }
                        } catch {
                            failed = .sanitized(error); halted = true; group.cancelAll()
                        }
                    }
                }
            }
        }
        if let failed { throw failed }
    }
}
