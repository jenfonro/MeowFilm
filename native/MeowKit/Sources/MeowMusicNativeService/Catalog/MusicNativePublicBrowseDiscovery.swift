// SPDX-License-Identifier: GPL-3.0-only
// Native consumers of the common runtime's canonical browse cards.
import Foundation
import MeowMusicFeature

enum MusicNativePublicBrowseContent: Sendable {
    case collection(MusicPublicBrowseCollection), song(MusicNativeTrackData), record(String)
    case channel(MusicPublicBrowseChannel), daily, unavailable
    case action(MusicHomeAction)
}
struct MusicNativePublicBrowseItem: Sendable {
    let id: String
    var title: String
    var subtitle: String = ""
    var artwork: URL?
    var content: MusicNativePublicBrowseContent
    var ranking: MusicPublicBrowseRanking? = nil
    var previewSongs: [MusicNativeTrackData] = []
    var recommendationReason = ""
    var coverLabel = ""
    var previewSongIDs: [String?] = []
    var requiresAccount = false
    var kind: MusicPublicBrowseItem.Kind? = nil
    var label = ""
    var contextText = ""
    var playCount: Int? = nil
    var presentation = ""
    var coverRef: MusicPublicBrowseItem.CoverReference? = nil
}
struct MusicNativePublicBrowseSectionTab: Sendable {
    let id: String
    let title: String
    var items: [MusicNativePublicBrowseItem]
}
struct MusicNativePublicBrowseSection: Sendable {
    let id: String
    let title: String
    var items: [MusicNativePublicBrowseItem]
    var layout: MusicPublicBrowseSection.Layout = .list
    var avatars: [URL] = []
    var channel: MusicPublicBrowseChannel? = nil
    var filters: [MusicPublicBrowseFilter] = []
    var selectedFilter = ""
    var error: MusicServiceError?
    var tabs: [MusicNativePublicBrowseSectionTab] = []
    var selectedTab = ""
}
struct MusicNativePublicBrowseDiscovery: Sendable {
    var sections: [MusicNativePublicBrowseSection] = []
    var next: MJ?
    var filters: [MusicPublicBrowseFilter] = []
    var selected = ""
    var notice: String? = nil
    var consumed: Int? = nil
    static func key(_ destination: MusicPublicBrowseDestination) -> String {
        switch destination {
        case .home: return "home"
        case .featured: return "featured"
        case .channels: return "channels"
        case .daily(let platform): return "daily:" + platform.rawValue
        case .channel(let channel, let tag): return "channel:\(channel.identity):\(channel.kind.rawValue):\(channel.pageCode):\(tag)"
        }
    }
}
