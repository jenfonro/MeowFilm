// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

/// One successful-page budget per business owner and search access generation.
enum MusicSearchResultsCache {
    struct Key: Hashable, Sendable {
        let kind: MusicSearchSection
        let platform: MusicPublicBrowsePlatform
        let query: String
        let request: MusicSearchPagination.Request
    }
    enum Page: Sendable {
        case songs(MusicSearchPage)
        case playlists(MusicPlaylistSearchPage)
    }
    typealias Storage = AsyncValueCache<Key, Page>

    static func make(clock: @escaping @Sendable () -> Date = { Date() }) -> Storage {
        .init(capacity: nil, maximumCost: 32 * 1024 * 1024, cost: { (value: Page) -> Int in
            switch value {
            case .songs(let page):
                var cost = page.query.utf8.count + 256
                for track in page.items {
                    cost += track.songId.utf8.count + track.title.utf8.count
                    cost += track.artist.utf8.count + track.album.utf8.count
                    cost += (track.artwork?.absoluteString.utf8.count ?? 0) + 256
                    for source in track.sources {
                        cost += source.sourceKey.utf8.count + source.nativeId.utf8.count + 64
                    }
                }
                return cost
            case .playlists(let page):
                var cost = page.query.utf8.count + 256
                for playlist in page.items {
                    cost += playlist.id.utf8.count + playlist.title.utf8.count
                    cost += playlist.creator.utf8.count + (playlist.artwork?.absoluteString.utf8.count ?? 0) + 192
                }
                return cost
            }
        }, clock: clock)
    }
}
#endif
