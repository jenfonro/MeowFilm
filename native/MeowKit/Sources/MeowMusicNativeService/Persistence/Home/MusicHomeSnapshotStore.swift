import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicHomeSnapshotStore {
    private static let itemInsertSQL: String = {
        let cols = [
            "item_uid", "section_id", "tab_id", "item_id", "position",
            "title", "subtitle", "artwork", "recommendation_reason", "cover_label",
            "requires_account", "kind", "label", "context_text", "play_count", "presentation",
            "cover_ref_platform", "cover_ref_kind", "cover_ref_id",
            "ranking_song_artwork", "ranking_update_frequency",
            "content_kind", "content_record",
            "content_col_platform", "content_col_native_id", "content_col_kind",
            "content_col_title", "content_col_creator", "content_col_artwork",
            "content_col_count", "content_col_play_count",
            "content_chan_id", "content_chan_title", "content_chan_kind",
            "content_chan_page_code", "content_chan_platform",
            "action_type", "action_provider", "action_mode", "action_submode",
            "action_entry", "action_rule_id", "action_tab_tag_id",
            "action_source_name", "action_source_type", "action_branch", "action_song_id"
        ]
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO ios_music_home_items(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func clear(_ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM ios_music_home_state")
        // Cascades to avatars, filters, tabs, items, item_lists, item_previews.
        try db.execute("DELETE FROM ios_music_home_sections")
    }

    static func read(_ db: SQLiteConnection) throws -> (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery)? {
        guard let state = try db.query("SELECT source FROM ios_music_home_state WHERE singleton=1").first else {
            return nil
        }
        guard let source = MusicPublicBrowsePlatform(rawValue: try state.text("source")) else {
            throw MusicServiceError.corruptDocument
        }

        let sectionRows = try db.query("SELECT * FROM ios_music_home_sections ORDER BY position")

        var avatarsBySection: [String: [URL]] = [:]
        for row in try db.query("SELECT section_id,avatar_url FROM ios_music_home_section_avatars ORDER BY section_id,position") {
            if let url = URL(string: try row.text("avatar_url")) {
                avatarsBySection[try row.text("section_id"), default: []].append(url)
            }
        }

        var filtersBySection: [String: [MusicPublicBrowseFilter]] = [:]
        for row in try db.query("SELECT section_id,filter_id,title,group_name FROM ios_music_home_section_filters ORDER BY section_id,position") {
            filtersBySection[try row.text("section_id"), default: []].append(
                MusicPublicBrowseFilter(
                    id: try row.text("filter_id"),
                    title: try row.text("title"),
                    group: try row.optionalText("group_name") ?? ""
                )
            )
        }

        var tabsBySection: [String: [(id: String, title: String)]] = [:]
        for row in try db.query("SELECT section_id,tab_id,title FROM ios_music_home_section_tabs ORDER BY section_id,position") {
            tabsBySection[try row.text("section_id"), default: []].append(
                (try row.text("tab_id"), try row.text("title"))
            )
        }

        var listsByItem: [String: [String: [String]]] = [:]
        for row in try db.query("SELECT item_uid,list_kind,item_value FROM ios_music_home_item_lists ORDER BY item_uid,list_kind,position") {
            listsByItem[try row.text("item_uid"), default: [:]][try row.text("list_kind"), default: []]
                .append(try row.text("item_value"))
        }

        var displayPreviewsByItem: [String: [MusicNativeTrackData.Display]] = [:]
        var rankingPreviewsByItem: [String: [MusicPublicBrowseRanking.Entry]] = [:]
        for row in try db.query(
            "SELECT item_uid,preview_kind,preview_id,title,artist,album,artwork,duration,rank,last_rank " +
            "FROM ios_music_home_item_previews ORDER BY item_uid,preview_kind,position"
        ) {
            let uid = try row.text("item_uid")
            let kind = try row.text("preview_kind")
            let artwork = (try row.optionalText("artwork")).flatMap(URL.init(string:))
            if kind == "displayOnly" {
                displayPreviewsByItem[uid, default: []].append(
                    MusicNativeTrackData.Display(
                        title: try row.text("title"),
                        artist: try row.text("artist"),
                        album: try row.optionalText("album") ?? "",
                        duration: try row.optionalDouble("duration") ?? 0,
                        artwork: artwork
                    )
                )
            } else {
                rankingPreviewsByItem[uid, default: []].append(
                    MusicPublicBrowseRanking.Entry(
                        id: try row.optionalText("preview_id") ?? "",
                        title: try row.text("title"),
                        artist: try row.text("artist"),
                        artwork: artwork,
                        rank: try row.optionalInteger("rank"),
                        lastRank: try row.optionalInteger("last_rank")
                    )
                )
            }
        }

        struct ParentKey: Hashable {
            let sectionID: String
            let tabID: String?
        }
        var itemsByParent: [ParentKey: [MusicNativePublicBrowseItem]] = [:]
        for row in try db.query("SELECT * FROM ios_music_home_items ORDER BY section_id,tab_id,position") {
            let uid = try row.text("item_uid")
            let sectionID = try row.text("section_id")
            let tabID = try row.optionalText("tab_id")
            let lists = listsByItem[uid] ?? [:]
            let content = try decodeContent(row, lists: lists)

            let kind: MusicPublicBrowseItem.Kind?
            if let rawKind = try row.optionalText("kind") {
                guard let parsed = MusicPublicBrowseItem.Kind(rawValue: rawKind) else {
                    throw MusicServiceError.corruptDocument
                }
                kind = parsed
            } else {
                kind = nil
            }

            let coverRef: MusicPublicBrowseItem.CoverReference?
            if let refPlatform = try row.optionalText("cover_ref_platform"),
               let refKind = try row.optionalText("cover_ref_kind"),
               let refID = try row.optionalText("cover_ref_id"),
               let platform = MusicPublicBrowsePlatform(rawValue: refPlatform),
               let colKind = MusicPublicBrowseCollection.Kind(rawValue: refKind) {
                coverRef = MusicPublicBrowseItem.CoverReference(platform: platform, kind: colKind, id: refID)
            } else {
                coverRef = nil
            }

            let rankingPreviews = rankingPreviewsByItem[uid] ?? []
            let rankingSongArtwork = (try row.optionalText("ranking_song_artwork")).flatMap(URL.init(string:))
            let rankingUpdateFrequency = try row.optionalText("ranking_update_frequency")
            let ranking: MusicPublicBrowseRanking?
            if rankingUpdateFrequency != nil || rankingSongArtwork != nil || !rankingPreviews.isEmpty {
                ranking = MusicPublicBrowseRanking(
                    preview: rankingPreviews,
                    songArtwork: rankingSongArtwork,
                    updateFrequency: rankingUpdateFrequency ?? ""
                )
            } else {
                ranking = nil
            }

            let previewSongIDs: [String?] = (lists["previewSongIDs"] ?? []).map { $0.isEmpty ? nil : $0 }
            let displayPreviews = (displayPreviewsByItem[uid] ?? []).map {
                MusicNativeTrackData(platform: "", data: Data("null".utf8), display: $0)
            }
            let item = MusicNativePublicBrowseItem(
                id: try row.text("item_id"),
                title: try row.text("title"),
                subtitle: try row.optionalText("subtitle") ?? "",
                artwork: (try row.optionalText("artwork")).flatMap(URL.init(string:)),
                content: content,
                ranking: ranking,
                previewSongs: displayPreviews,
                recommendationReason: try row.optionalText("recommendation_reason") ?? "",
                coverLabel: try row.optionalText("cover_label") ?? "",
                previewSongIDs: previewSongIDs,
                requiresAccount: try row.optionalBool("requires_account") ?? false,
                kind: kind,
                label: try row.optionalText("label") ?? "",
                contextText: try row.optionalText("context_text") ?? "",
                playCount: try row.optionalInteger("play_count"),
                presentation: try row.optionalText("presentation") ?? "",
                coverRef: coverRef
            )
            itemsByParent[ParentKey(sectionID: sectionID, tabID: tabID), default: []].append(item)
        }

        var sections: [MusicNativePublicBrowseSection] = []
        sections.reserveCapacity(sectionRows.count)
        for row in sectionRows {
            let sectionID = try row.text("section_id")
            guard let layout = MusicPublicBrowseSection.Layout(rawValue: try row.text("layout")) else {
                throw MusicServiceError.corruptDocument
            }
            let channel: MusicPublicBrowseChannel?
            if let chID = try row.optionalText("channel_id"),
               let chKindRaw = try row.optionalText("channel_kind"),
               let chKind = MusicPublicBrowseChannel.Kind(rawValue: chKindRaw),
               let chPlatformRaw = try row.optionalText("channel_platform"),
               let chPlatform = MusicPublicBrowsePlatform(rawValue: chPlatformRaw) {
                channel = MusicPublicBrowseChannel(
                    id: chID,
                    title: try row.optionalText("channel_title") ?? "",
                    kind: chKind,
                    pageCode: try row.optionalText("channel_page_code") ?? "",
                    platform: chPlatform
                )
            } else {
                channel = nil
            }
            let tabs = (tabsBySection[sectionID] ?? []).map { tab in
                MusicNativePublicBrowseSectionTab(
                    id: tab.id,
                    title: tab.title,
                    items: itemsByParent[ParentKey(sectionID: sectionID, tabID: tab.id)] ?? []
                )
            }
            sections.append(
                MusicNativePublicBrowseSection(
                    id: sectionID,
                    title: try row.text("title"),
                    items: itemsByParent[ParentKey(sectionID: sectionID, tabID: nil)] ?? [],
                    layout: layout,
                    avatars: avatarsBySection[sectionID] ?? [],
                    channel: channel,
                    filters: filtersBySection[sectionID] ?? [],
                    selectedFilter: try row.optionalText("selected_filter") ?? "",
                    tabs: tabs,
                    selectedTab: try row.optionalText("selected_tab") ?? ""
                )
            )
        }

        return (source, MusicNativePublicBrowseDiscovery(sections: sections))
    }

    static func write(_ db: SQLiteConnection,
                      page: MusicNativePublicBrowseDiscovery,
                      source: MusicPublicBrowsePlatform) throws {
        try clear(db)
        try db.upsert(
            "ios_music_home_state",
            columns: ["singleton", "source"],
            values: [.integer(1), .text(source.rawValue)],
            key: ["singleton"]
        )

        var seenSections = Set<String>()
        for (secIndex, section) in page.sections.enumerated() {
            guard seenSections.insert(section.id).inserted else { continue }
            try db.execute(
                "INSERT INTO ios_music_home_sections(" +
                "section_id,position,title,layout,selected_filter,selected_tab," +
                "channel_id,channel_title,channel_kind,channel_page_code,channel_platform" +
                ") VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                [
                    .text(section.id),
                    .integer(secIndex),
                    .text(section.title),
                    .text(section.layout.rawValue),
                    section.selectedFilter.isEmpty ? .null : .text(section.selectedFilter),
                    section.selectedTab.isEmpty ? .null : .text(section.selectedTab),
                    .optional(section.channel?.id),
                    .optional(section.channel?.title),
                    .optional(section.channel?.kind.rawValue),
                    .optional(section.channel?.pageCode),
                    .optional(section.channel?.platform.rawValue)
                ]
            )

            for (idx, avatar) in section.avatars.enumerated() {
                let safe = MusicModel.publicURL(.string(avatar.absoluteString)).string
                guard !safe.isEmpty else { continue }
                try db.execute(
                    "INSERT INTO ios_music_home_section_avatars(section_id,position,avatar_url) VALUES(?,?,?)",
                    [.text(section.id), .integer(idx), .text(safe)]
                )
            }

            var seenFilters = Set<String>()
            for (idx, filter) in section.filters.enumerated() {
                guard seenFilters.insert(filter.id).inserted else { continue }
                try db.execute(
                    "INSERT INTO ios_music_home_section_filters(section_id,filter_id,position,title,group_name) VALUES(?,?,?,?,?)",
                    [
                        .text(section.id), .text(filter.id), .integer(idx),
                        .text(filter.title), filter.group.isEmpty ? .null : .text(filter.group)
                    ]
                )
            }

            var seenTabs = Set<String>()
            for (tabIdx, tab) in section.tabs.enumerated() {
                guard seenTabs.insert(tab.id).inserted else { continue }
                try db.execute(
                    "INSERT INTO ios_music_home_section_tabs(section_id,tab_id,position,title) VALUES(?,?,?,?)",
                    [.text(section.id), .text(tab.id), .integer(tabIdx), .text(tab.title)]
                )
                for (itemIdx, item) in tab.items.enumerated() {
                    try writeItem(db, sectionID: section.id, secIndex: secIndex, tabID: tab.id, tabIndex: tabIdx, itemIndex: itemIdx, item: item)
                }
            }

            for (itemIdx, item) in section.items.enumerated() {
                try writeItem(db, sectionID: section.id, secIndex: secIndex, tabID: nil, tabIndex: nil, itemIndex: itemIdx, item: item)
            }
        }
    }

    static func estimatedBytes(_ page: MusicNativePublicBrowseDiscovery) -> Int {
        var size = 64
        func itemBytes(_ item: MusicNativePublicBrowseItem) -> Int {
            var n = 256 + item.id.utf8.count + item.title.utf8.count + item.subtitle.utf8.count
            n += (item.artwork?.absoluteString.utf8.count ?? 0) + item.recommendationReason.utf8.count
            n += item.coverLabel.utf8.count + item.label.utf8.count + item.contextText.utf8.count + item.presentation.utf8.count
            n += item.previewSongIDs.reduce(0) { $0 + ($1?.utf8.count ?? 0) + 8 }
            n += item.previewSongs.reduce(0) { $0 + 128 + ($1.display?.title.utf8.count ?? 0) + ($1.display?.artist.utf8.count ?? 0) }
            if let ranking = item.ranking {
                n += 64 + ranking.updateFrequency.utf8.count + ranking.preview.count * 128
            }
            return n
        }
        for section in page.sections {
            size += 128 + section.id.utf8.count + section.title.utf8.count
            size += section.avatars.reduce(0) { $0 + $1.absoluteString.utf8.count + 16 }
            size += section.filters.reduce(0) { $0 + $1.id.utf8.count + $1.title.utf8.count + $1.group.utf8.count + 32 }
            size += section.items.reduce(0) { $0 + itemBytes($1) }
            for tab in section.tabs {
                size += 64 + tab.id.utf8.count + tab.title.utf8.count
                size += tab.items.reduce(0) { $0 + itemBytes($1) }
            }
        }
        return size
    }

    private static func writeItem(_ db: SQLiteConnection,
                                  sectionID: String,
                                  secIndex: Int,
                                  tabID: String?,
                                  tabIndex: Int?,
                                  itemIndex: Int,
                                  item: MusicNativePublicBrowseItem) throws {
        let uid = "\(secIndex):\(tabIndex ?? -1):\(itemIndex)"
        let title: String
        let subtitle: String
        let artwork: String?
        if case .record = item.content, item.kind == nil {
            title = ""; subtitle = ""; artwork = nil
        } else {
            title = item.title
            subtitle = item.subtitle
            let sanitized = MusicModel.publicURL(.string(item.artwork?.absoluteString ?? "")).string
            artwork = sanitized.isEmpty ? nil : sanitized
        }

        var contentKind = "unavailable"
        var contentRecord: String?
        var colPlatform: String?, colNativeID: String?, colKind: String?, colTitle: String?
        var colCreator: String?, colArtwork: String?, colCount: Int?, colPlayCount: Int?
        var chanID: String?, chanTitle: String?, chanKind: String?, chanPageCode: String?, chanPlatform: String?
        var actionType: String?, actionProvider: String?, actionMode: String?, actionSubmode: String?
        var actionEntry: String?, actionRuleID: String?, actionTabTagID: String?
        var actionSourceName: String?, actionSourceType: String?, actionBranch: String?, actionSongID: String?
        var actionSourceIDs: [String] = []
        var actionIDs: [String] = []

        switch item.content {
        case .collection(let col):
            contentKind = "collection"
            colPlatform = col.platform.rawValue
            colNativeID = col.nativeID
            colKind = col.kind.rawValue
            colTitle = col.title
            colCreator = col.creator
            let safeArt = MusicModel.publicURL(.string(col.artwork?.absoluteString ?? "")).string
            colArtwork = safeArt.isEmpty ? nil : safeArt
            colCount = col.count
            colPlayCount = col.playCount
        case .record(let id):
            contentKind = "record"
            contentRecord = id
        case .channel(let ch):
            contentKind = "channel"
            chanID = ch.id
            chanTitle = ch.title
            chanKind = ch.kind.rawValue
            chanPageCode = ch.pageCode
            chanPlatform = ch.platform.rawValue
        case .daily:
            contentKind = "daily"
        case .unavailable:
            contentKind = "unavailable"
        case .action(let action):
            contentKind = "action"
            switch action {
            case .fm(let mode, let submode, let entry):
                actionType = "fm"
                actionMode = mode.rawValue
                actionSubmode = submode
                actionEntry = entry?.rawValue
            case .ruleFM(let ruleID, let tabTagID, let sourceName):
                actionType = "fm"
                actionProvider = "rule"
                actionRuleID = ruleID
                actionTabTagID = tabTagID
                actionSourceName = sourceName
            case .similar(let sourceIDs, let sourceName):
                actionType = "similar"
                actionSourceType = "song"
                actionSourceName = sourceName.isEmpty ? nil : sourceName
                actionSourceIDs = sourceIDs
            case .similarArtist(let sourceIDs, let sourceName):
                actionType = "similar"
                actionSourceType = "artist"
                actionSourceName = sourceName.isEmpty ? nil : sourceName
                actionSourceIDs = sourceIDs
            case .daily(let branch):
                actionType = "daily"
                actionBranch = branch.rawValue
            case .heartbeat:
                actionType = "heartbeat"
            case .heartbeatSong(let songID):
                actionType = "heartbeat"
                actionSongID = songID
            case .songCollection(let ids):
                actionType = "song-collection"
                actionIDs = ids
            }
        case .song:
            throw MusicServiceError.invalidRequest
        }

        let rankingSongArtwork: String?
        if let raw = item.ranking?.songArtwork?.absoluteString {
            let safe = MusicModel.publicURL(.string(raw)).string
            rankingSongArtwork = safe.isEmpty ? nil : safe
        } else {
            rankingSongArtwork = nil
        }

        let values: [SQLiteValue] = [
            .text(uid), .text(sectionID), .optional(tabID), .text(item.id), .integer(itemIndex),
            .text(title), subtitle.isEmpty ? .null : .text(subtitle), .optional(artwork),
            item.recommendationReason.isEmpty ? .null : .text(item.recommendationReason),
            item.coverLabel.isEmpty ? .null : .text(item.coverLabel),
            .bool(item.requiresAccount), .optional(item.kind?.rawValue),
            item.label.isEmpty ? .null : .text(item.label),
            item.contextText.isEmpty ? .null : .text(item.contextText),
            .optional(item.playCount),
            item.presentation.isEmpty ? .null : .text(item.presentation),
            .optional(item.coverRef?.platform.rawValue),
            .optional(item.coverRef?.kind.rawValue),
            .optional(item.coverRef?.id),
            .optional(rankingSongArtwork),
            .optional(item.ranking?.updateFrequency),
            .text(contentKind), .optional(contentRecord),
            .optional(colPlatform), .optional(colNativeID), .optional(colKind),
            .optional(colTitle), .optional(colCreator), .optional(colArtwork),
            .optional(colCount), .optional(colPlayCount),
            .optional(chanID), .optional(chanTitle), .optional(chanKind),
            .optional(chanPageCode), .optional(chanPlatform),
            .optional(actionType), .optional(actionProvider), .optional(actionMode), .optional(actionSubmode),
            .optional(actionEntry), .optional(actionRuleID), .optional(actionTabTagID),
            .optional(actionSourceName), .optional(actionSourceType), .optional(actionBranch), .optional(actionSongID)
        ]
        try db.execute(itemInsertSQL, values)

        for (idx, id) in item.previewSongIDs.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_home_item_lists(item_uid,list_kind,position,item_value) VALUES(?,?,?,?)",
                [.text(uid), .text("previewSongIDs"), .integer(idx), .text(id ?? "")]
            )
        }
        for (idx, id) in actionSourceIDs.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_home_item_lists(item_uid,list_kind,position,item_value) VALUES(?,?,?,?)",
                [.text(uid), .text("actionSourceIds"), .integer(idx), .text(id)]
            )
        }
        for (idx, id) in actionIDs.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_home_item_lists(item_uid,list_kind,position,item_value) VALUES(?,?,?,?)",
                [.text(uid), .text("actionIds"), .integer(idx), .text(id)]
            )
        }

        for (idx, display) in item.previewSongs.compactMap(\.display).enumerated() {
            let safeArt = MusicModel.publicURL(.string(display.artwork?.absoluteString ?? "")).string
            try db.execute(
                "INSERT INTO ios_music_home_item_previews(" +
                "item_uid,preview_kind,position,preview_id,title,artist,album,artwork,duration,rank,last_rank" +
                ") VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                [
                    .text(uid), .text("displayOnly"), .integer(idx), .null,
                    .text(display.title), .text(display.artist), .text(display.album),
                    safeArt.isEmpty ? .null : .text(safeArt), .real(display.duration), .null, .null
                ]
            )
        }
        if let ranking = item.ranking {
            for (idx, entry) in ranking.preview.enumerated() {
                let safeArt = MusicModel.publicURL(.string(entry.artwork?.absoluteString ?? "")).string
                try db.execute(
                    "INSERT INTO ios_music_home_item_previews(" +
                    "item_uid,preview_kind,position,preview_id,title,artist,album,artwork,duration,rank,last_rank" +
                    ") VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    [
                        .text(uid), .text("ranking"), .integer(idx), .text(entry.id),
                        .text(entry.title), .text(entry.artist), .null,
                        safeArt.isEmpty ? .null : .text(safeArt), .null,
                        .optional(entry.rank), .optional(entry.lastRank)
                    ]
                )
            }
        }
    }

    private static func decodeContent(_ row: SQLiteRow, lists: [String: [String]]) throws -> MusicNativePublicBrowseContent {
        let kind = try row.optionalText("content_kind") ?? "unavailable"
        switch kind {
        case "collection":
            guard let platformRaw = try row.optionalText("content_col_platform"),
                  let platform = MusicPublicBrowsePlatform(rawValue: platformRaw),
                  let nativeID = try row.optionalText("content_col_native_id"),
                  let colKindRaw = try row.optionalText("content_col_kind"),
                  let colKind = MusicPublicBrowseCollection.Kind(rawValue: colKindRaw) else {
                throw MusicServiceError.corruptDocument
            }
            return .collection(
                MusicPublicBrowseCollection(
                    platform: platform,
                    nativeID: nativeID,
                    kind: colKind,
                    title: try row.optionalText("content_col_title") ?? "",
                    creator: try row.optionalText("content_col_creator") ?? "",
                    artwork: (try row.optionalText("content_col_artwork")).flatMap(URL.init(string:)),
                    count: try row.optionalInteger("content_col_count"),
                    playCount: try row.optionalInteger("content_col_play_count")
                )
            )
        case "record":
            guard let id = try row.optionalText("content_record") else { throw MusicServiceError.corruptDocument }
            return .record(id)
        case "channel":
            guard let chID = try row.optionalText("content_chan_id"),
                  let chKindRaw = try row.optionalText("content_chan_kind"),
                  let chKind = MusicPublicBrowseChannel.Kind(rawValue: chKindRaw),
                  let chPlatformRaw = try row.optionalText("content_chan_platform"),
                  let chPlatform = MusicPublicBrowsePlatform(rawValue: chPlatformRaw) else {
                throw MusicServiceError.corruptDocument
            }
            return .channel(
                MusicPublicBrowseChannel(
                    id: chID,
                    title: try row.optionalText("content_chan_title") ?? "",
                    kind: chKind,
                    pageCode: try row.optionalText("content_chan_page_code") ?? "",
                    platform: chPlatform
                )
            )
        case "daily":
            return .daily
        case "unavailable":
            return .unavailable
        case "action":
            return .action(try decodeAction(row, lists: lists))
        default:
            throw MusicServiceError.corruptDocument
        }
    }

    private static func decodeAction(_ row: SQLiteRow, lists: [String: [String]]) throws -> MusicHomeAction {
        guard let type = try row.optionalText("action_type") else { throw MusicServiceError.corruptDocument }
        switch type {
        case "fm":
            let provider = try row.optionalText("action_provider") ?? ""
            if provider == "rule" {
                guard let ruleID = try row.optionalText("action_rule_id"),
                      let tabTagID = try row.optionalText("action_tab_tag_id") else {
                    throw MusicServiceError.corruptDocument
                }
                return .ruleFM(ruleID: ruleID, tabTagID: tabTagID, sourceName: try row.optionalText("action_source_name") ?? "")
            }
            guard provider.isEmpty,
                  let modeRaw = try row.optionalText("action_mode"),
                  let mode = MusicHomeAction.FMMode(rawValue: modeRaw) else {
                throw MusicServiceError.corruptDocument
            }
            let entry: MusicHomeAction.FMEntry?
            if let entryRaw = try row.optionalText("action_entry") {
                guard let parsed = MusicHomeAction.FMEntry(rawValue: entryRaw) else {
                    throw MusicServiceError.corruptDocument
                }
                entry = parsed
            } else {
                entry = nil
            }
            return .fm(mode: mode, submode: try row.optionalText("action_submode") ?? "", entry: entry)
        case "similar":
            let sourceType = try row.optionalText("action_source_type") ?? ""
            let ids = lists["actionSourceIds"] ?? []
            let name = try row.optionalText("action_source_name") ?? ""
            switch sourceType {
            case "song": return .similar(sourceIDs: ids, sourceName: name)
            case "artist": return .similarArtist(sourceIDs: ids, sourceName: name)
            default: throw MusicServiceError.corruptDocument
            }
        case "daily":
            guard let branchRaw = try row.optionalText("action_branch"),
                  let branch = MusicHomeAction.DailyBranch(rawValue: branchRaw) else {
                throw MusicServiceError.corruptDocument
            }
            return .daily(branch: branch)
        case "heartbeat":
            if let songID = try row.optionalText("action_song_id") {
                return .heartbeatSong(songID: songID)
            }
            return .heartbeat
        case "song-collection":
            return .songCollection(ids: lists["actionIds"] ?? [])
        default:
            throw MusicServiceError.corruptDocument
        }
    }
}
