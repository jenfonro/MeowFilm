// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation

/// Reconstruct only an expired opaque cursor, not a new search/result set.
/// Every previously accepted page must retain its ordered native identities.
/// A changed prefix fails visibly while the caller retains its displayed rows.
enum MusicSearchPlaylistRecovery {
    @MainActor static func read(platform: MusicPublicBrowsePlatform, query: String, cursor: String?,
        acceptedPages: [[String]], reader: MusicSearchResultsModel.PlaylistRead) async throws -> MusicPublicBrowseCollectionPage {
        do {
            let page = try await reader(platform, query, cursor)
            try validate(page, platform: platform, cursor: cursor)
            return page
        } catch {
            guard !Task.isCancelled, error as? MusicServiceError == .handleExpired,
                  cursor != nil, !acceptedPages.isEmpty else { throw error }
        }
        guard acceptedPages.count < 100 else { throw MusicServiceError.limit }
        var rebuilt: String?, seen = Set<Data>()
        for identities in acceptedPages {
            try Task.checkCancellation()
            let page = try await reader(platform, query, rebuilt)
            try Task.checkCancellation()
            try validate(page, platform: platform, cursor: rebuilt)
            guard page.items.map({ Data($0.id.utf8) }) == identities.map({ Data($0.utf8) }),
                  let next = page.cursor else { throw MusicServiceError.staleResult }
            guard seen.insert(Data(next.utf8)).inserted else { throw MusicServiceError.invalidResponse }
            rebuilt = next
        }
        // Exactly one replay per attempt. A second expiry/error propagates;
        // there is no reset/retry loop and no partially published rebuild.
        try Task.checkCancellation()
        let page = try await reader(platform, query, rebuilt)
        try Task.checkCancellation()
        try validate(page, platform: platform, cursor: rebuilt)
        if let next = page.cursor, seen.contains(Data(next.utf8)) { throw MusicServiceError.invalidResponse }
        return page
    }
    private static func validate(_ page: MusicPublicBrowseCollectionPage,
                                 platform: MusicPublicBrowsePlatform, cursor: String?) throws {
        guard page.items.count <= 200, page.items.allSatisfy({ $0.platform == platform }),
              page.cursor == nil || page.cursor != cursor else { throw MusicServiceError.invalidResponse }
    }
}
#endif
