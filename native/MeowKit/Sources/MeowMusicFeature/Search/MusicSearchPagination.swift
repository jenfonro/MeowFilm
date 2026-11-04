// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Search rounds request one platform page at its configured page size.
/// The limit describes that single page; progress never splits or combines pages.
public enum MusicSearchPagination {
    public struct Request: Hashable, Sendable {
        public let page: Int
        public let limit: Int
        public var nextPage: Int? {
            let (next, overflow) = page.addingReportingOverflow(1)
            return overflow ? nil : next
        }
        public init(page: Int, limit: Int) {
            self.page = page; self.limit = limit
        }
    }
    public static func pageSize(_ platform: MusicPublicBrowsePlatform) -> Int { 30 }
    public static func offset(platform: MusicPublicBrowsePlatform, page: Int, limit: Int) throws -> Int {
        guard page > 0, (1...100).contains(limit) else { throw MusicServiceError.invalidRequest }
        let (offset, overflow) = (page - 1).multipliedReportingOverflow(by: limit)
        guard !overflow, offset <= Int.max - limit else { throw MusicServiceError.invalidRequest }
        return offset
    }
    public static func validNextPage(page: Int, limit: Int, next: Int?, hasItems: Bool) -> Bool {
        guard page > 0, (1...100).contains(limit) else { return false }
        guard let next else { return true }
        let (expected, overflow) = page.addingReportingOverflow(1)
        return !overflow && hasItems && next == expected
    }
    /// A short raw page is terminal. A full page ends when its end offset
    /// has reached or exceeded the reported total. Empty pages
    /// are a normal end, even when the reported total is still larger.
    public static func nextPage(page: Int, limit: Int, count: Int, total: Int) throws -> Int? {
        guard page > 0, (1...100).contains(limit), count >= 0, count <= limit, total >= 0 else {
            throw MusicServiceError.upstreamInvalid
        }
        let (offset, overflow) = (page - 1).multipliedReportingOverflow(by: limit)
        guard !overflow, offset <= Int.max - limit else {
            throw MusicServiceError.upstreamInvalid
        }
        guard count == limit, offset + count < total else { return nil }
        let (next, nextOverflow) = page.addingReportingOverflow(1)
        return nextOverflow ? nil : next
    }
}

public struct MusicPlaylistSearchPage: Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let query: String
    public let page: Int
    public let limit: Int
    public let total: Int
    public let nextPage: Int?
    public let items: [MusicPublicBrowseCollection]
    public init(platform: MusicPublicBrowsePlatform, query: String, page: Int, limit: Int = 30,
                total: Int, nextPage: Int?, items: [MusicPublicBrowseCollection]) {
        self.platform = platform; self.query = query; self.page = page; self.limit = limit
        self.total = total; self.nextPage = nextPage; self.items = items
    }
}

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
