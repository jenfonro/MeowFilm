import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicHomeSnapshotStore {
    private static let itemColumns = [
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

    private static let itemInsertSQL: String = {
        let placeholders = Array(repeating: "?", count: itemColumns.count).joined(separator: ",")
        return "INSERT INTO ios_music_home_items(\(itemColumns.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let itemSelectSQL: String =
        "SELECT " + itemColumns.joined(separator: ",") +
        " FROM ios_music_home_items ORDER BY section_id,tab_id,position"

    static func clear(_ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM ios_music_home_state")
        // Cascades to avatars, filters, tabs, items, item_lists, item_previews.
        try db.execute("DELETE FROM ios_music_home_sections")
    }

    static func read(_ db: SQLiteConnection) throws -> (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery)? {
        guard let sourceRaw = try db.queryOne(
            "SELECT source FROM ios_music_home_state WHERE singleton=1",
            map: { try $0.text(0) }
        ) else {
            return nil
        }
        guard let source = MusicPublicBrowsePlatform(rawValue: sourceRaw) else {
            throw MusicServiceError.corruptStorage
        }

        let sectionRows: [(
            id: String, title: String, layoutRaw: String, selectedFilter: String?, selectedTab: String?,
            channelID: String?, channelTitle: String?, channelKindRaw: String?, channelPageCode: String?, channelPlatformRaw: String?
        )] = try db.query(
            "SELECT section_id,title,layout,selected_filter,selected_tab," +
            "channel_id,channel_title,channel_kind,channel_page_code,channel_platform " +
            "FROM ios_music_home_sections ORDER BY position"
        ) { c in
            (
                id: try c.text(0),
                title: try c.text(1),
                layoutRaw: try c.text(2),
                selectedFilter: try c.optionalText(3),
                selectedTab: try c.optionalText(4),
                channelID: try c.optionalText(5),
                channelTitle: try c.optionalText(6),
                channelKindRaw: try c.optionalText(7),
                channelPageCode: try c.optionalText(8),
                channelPlatformRaw: try c.optionalText(9)
            )
        }

        var avatarsBySection: [String: [URL]] = [:]
        try db.scan("SELECT section_id,avatar_url FROM ios_music_home_section_avatars ORDER BY section_id,position") { c in
            let sectionID = try c.text(0)
            if let url = URL(string: try c.text(1)) {
                avatarsBySection[sectionID, default: []].append(url)
            }
        }

        var filtersBySection: [String: [MusicPublicBrowseFilter]] = [:]
        try db.scan("SELECT section_id,filter_id,title,group_name FROM ios_music_home_section_filters ORDER BY section_id,position") { c in
            let sectionID = try c.text(0)
            filtersBySection[sectionID, default: []].append(
                MusicPublicBrowseFilter(
                    id: try c.text(1),
                    title: try c.text(2),
                    group: try c.optionalText(3) ?? ""
                )
            )
        }

        var tabsBySection: [String: [(id: String, title: String)]] = [:]
        try db.scan("SELECT section_id,tab_id,title FROM ios_music_home_section_tabs ORDER BY section_id,position") { c in
            tabsBySection[try c.text(0), default: []].append((try c.text(1), try c.text(2)))
        }

        var listsByItem: [String: [String: [String]]] = [:]
        try db.scan("SELECT item_uid,list_kind,item_value FROM ios_music_home_item_lists ORDER BY item_uid,list_kind,position") { c in
            listsByItem[try c.text(0), default: [:]][try c.text(1), default: []].append(try c.text(2))
        }

        var displayPreviewsByItem: [String: [MusicNativeTrackData.Display]] = [:]
        var rankingPreviewsByItem: [String: [MusicPublicBrowseRanking.Entry]] = [:]
        try db.scan(
            "SELECT item_uid,preview_kind,preview_id,title,artist,album,artwork,duration,rank,last_rank " +
            "FROM ios_music_home_item_previews ORDER BY item_uid,preview_kind,position"
        ) { c in
            let uid = try c.text(0)
            let kind = try c.text(1)
            let artwork = (try c.optionalText(6)).flatMap(URL.init(string:))
            if kind == "displayOnly" {
                displayPreviewsByItem[uid, default: []].append(
                    MusicNativeTrackData.Display(
                        title: try c.text(3),
                        artist: try c.text(4),
                        album: try c.optionalText(5) ?? "",
                        duration: try c.optionalDouble(7) ?? 0,
                        artwork: artwork
                    )
                )
            } else {
                rankingPreviewsByItem[uid, default: []].append(
                    MusicPublicBrowseRanking.Entry(
                        id: try c.optionalText(2) ?? "",
                        title: try c.text(3),
                        artist: try c.text(4),
                        artwork: artwork,
                        rank: try c.optionalInteger(8),
                        lastRank: try c.optionalInteger(9)
                    )
                )
            }
        }

        struct ParentKey: Hashable {
            let sectionID: String
            let tabID: String?
        }
        var itemsByParent: [ParentKey: [MusicNativePublicBrowseItem]] = [:]
        try db.scan(itemSelectSQL) { c in
            let uid = try c.text(0)
            let sectionID = try c.text(1)
            let tabID = try c.optionalText(2)
            let lists = listsByItem[uid] ?? [:]
            let content = try decodeContent(c, lists: lists)

            let kind: MusicPublicBrowseItem.Kind?
            if let rawKind = try c.optionalText(11) {
                guard let parsed = MusicPublicBrowseItem.Kind(rawValue: rawKind) else {
                    throw MusicServiceError.corruptStorage
                }
                kind = parsed
            } else {
                kind = nil
            }

            let coverRef: MusicPublicBrowseItem.CoverReference?
            if let refPlatform = try c.optionalText(16),
               let refKind = try c.optionalText(17),
               let refID = try c.optionalText(18),
               let platform = MusicPublicBrowsePlatform(rawValue: refPlatform),
               let colKind = MusicPublicBrowseCollection.Kind(rawValue: refKind) {
                coverRef = MusicPublicBrowseItem.CoverReference(platform: platform, kind: colKind, id: refID)
            } else {
                coverRef = nil
            }

            let rankingPreviews = rankingPreviewsByItem[uid] ?? []
            let rankingSongArtwork = (try c.optionalText(19)).flatMap(URL.init(string:))
            let rankingUpdateFrequency = try c.optionalText(20)
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
                id: try c.text(3),
                title: try c.text(5),
                subtitle: try c.optionalText(6) ?? "",
                artwork: (try c.optionalText(7)).flatMap(URL.init(string:)),
                content: content,
                ranking: ranking,
                previewSongs: displayPreviews,
                recommendationReason: try c.optionalText(8) ?? "",
                coverLabel: try c.optionalText(9) ?? "",
                previewSongIDs: previewSongIDs,
                requiresAccount: try c.optionalBool(10) ?? false,
                kind: kind,
                label: try c.optionalText(12) ?? "",
                contextText: try c.optionalText(13) ?? "",
                playCount: try c.optionalInteger(14),
                presentation: try c.optionalText(15) ?? "",
                coverRef: coverRef
            )
            itemsByParent[ParentKey(sectionID: sectionID, tabID: tabID), default: []].append(item)
        }

        var sections: [MusicNativePublicBrowseSection] = []
        sections.reserveCapacity(sectionRows.count)
        for row in sectionRows {
            guard let layout = MusicPublicBrowseSection.Layout(rawValue: row.layoutRaw) else {
                throw MusicServiceError.corruptStorage
            }
            let channel: MusicPublicBrowseChannel?
            if let chID = row.channelID,
               let chKindRaw = row.channelKindRaw,
               let chKind = MusicPublicBrowseChannel.Kind(rawValue: chKindRaw),
               let chPlatformRaw = row.channelPlatformRaw,
               let chPlatform = MusicPublicBrowsePlatform(rawValue: chPlatformRaw) {
                channel = MusicPublicBrowseChannel(
                    id: chID,
                    title: row.channelTitle ?? "",
                    kind: chKind,
                    pageCode: row.channelPageCode ?? "",
                    platform: chPlatform
                )
            } else {
                channel = nil
            }
            let tabs = (tabsBySection[row.id] ?? []).map { tab in
                MusicNativePublicBrowseSectionTab(
                    id: tab.id,
                    title: tab.title,
                    items: itemsByParent[ParentKey(sectionID: row.id, tabID: tab.id)] ?? []
                )
            }
            sections.append(
                MusicNativePublicBrowseSection(
                    id: row.id,
                    title: row.title,
                    items: itemsByParent[ParentKey(sectionID: row.id, tabID: nil)] ?? [],
                    layout: layout,
                    avatars: avatarsBySection[row.id] ?? [],
                    channel: channel,
                    filters: filtersBySection[row.id] ?? [],
                    selectedFilter: row.selectedFilter ?? "",
                    tabs: tabs,
                    selectedTab: row.selectedTab ?? ""
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
                let safe = MusicModel.publicURLString(avatar.absoluteString)
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
            let sanitized = MusicModel.publicURLString(item.artwork?.absoluteString ?? "")
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
            let safeArt = MusicModel.publicURLString(col.artwork?.absoluteString ?? "")
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
            let safe = MusicModel.publicURLString(raw)
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
            let safeArt = MusicModel.publicURLString(display.artwork?.absoluteString ?? "")
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
                let safeArt = MusicModel.publicURLString(entry.artwork?.absoluteString ?? "")
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

    private static func decodeContent(_ c: SQLiteCursor, lists: [String: [String]]) throws -> MusicNativePublicBrowseContent {
        let kind = try c.optionalText(21) ?? "unavailable"
        switch kind {
        case "collection":
            guard let platformRaw = try c.optionalText(23),
                  let platform = MusicPublicBrowsePlatform(rawValue: platformRaw),
                  let nativeID = try c.optionalText(24),
                  let colKindRaw = try c.optionalText(25),
                  let colKind = MusicPublicBrowseCollection.Kind(rawValue: colKindRaw) else {
                throw MusicServiceError.corruptStorage
            }
            return .collection(
                MusicPublicBrowseCollection(
                    platform: platform,
                    nativeID: nativeID,
                    kind: colKind,
                    title: try c.optionalText(26) ?? "",
                    creator: try c.optionalText(27) ?? "",
                    artwork: (try c.optionalText(28)).flatMap(URL.init(string:)),
                    count: try c.optionalInteger(29),
                    playCount: try c.optionalInteger(30)
                )
            )
        case "record":
            guard let id = try c.optionalText(22) else { throw MusicServiceError.corruptStorage }
            return .record(id)
        case "channel":
            guard let chID = try c.optionalText(31),
                  let chKindRaw = try c.optionalText(33),
                  let chKind = MusicPublicBrowseChannel.Kind(rawValue: chKindRaw),
                  let chPlatformRaw = try c.optionalText(35),
                  let chPlatform = MusicPublicBrowsePlatform(rawValue: chPlatformRaw) else {
                throw MusicServiceError.corruptStorage
            }
            return .channel(
                MusicPublicBrowseChannel(
                    id: chID,
                    title: try c.optionalText(32) ?? "",
                    kind: chKind,
                    pageCode: try c.optionalText(34) ?? "",
                    platform: chPlatform
                )
            )
        case "daily":
            return .daily
        case "unavailable":
            return .unavailable
        case "action":
            return .action(try decodeAction(c, lists: lists))
        default:
            throw MusicServiceError.corruptStorage
        }
    }

    private static func decodeAction(_ c: SQLiteCursor, lists: [String: [String]]) throws -> MusicHomeAction {
        guard let type = try c.optionalText(36) else { throw MusicServiceError.corruptStorage }
        switch type {
        case "fm":
            let provider = try c.optionalText(37) ?? ""
            if provider == "rule" {
                guard let ruleID = try c.optionalText(41),
                      let tabTagID = try c.optionalText(42) else {
                    throw MusicServiceError.corruptStorage
                }
                return .ruleFM(ruleID: ruleID, tabTagID: tabTagID, sourceName: try c.optionalText(43) ?? "")
            }
            guard provider.isEmpty,
                  let modeRaw = try c.optionalText(38),
                  let mode = MusicHomeAction.FMMode(rawValue: modeRaw) else {
                throw MusicServiceError.corruptStorage
            }
            let entry: MusicHomeAction.FMEntry?
            if let entryRaw = try c.optionalText(40) {
                guard let parsed = MusicHomeAction.FMEntry(rawValue: entryRaw) else {
                    throw MusicServiceError.corruptStorage
                }
                entry = parsed
            } else {
                entry = nil
            }
            return .fm(mode: mode, submode: try c.optionalText(39) ?? "", entry: entry)
        case "similar":
            let sourceType = try c.optionalText(44) ?? ""
            let ids = lists["actionSourceIds"] ?? []
            let name = try c.optionalText(43) ?? ""
            switch sourceType {
            case "song": return .similar(sourceIDs: ids, sourceName: name)
            case "artist": return .similarArtist(sourceIDs: ids, sourceName: name)
            default: throw MusicServiceError.corruptStorage
            }
        case "daily":
            guard let branchRaw = try c.optionalText(45),
                  let branch = MusicHomeAction.DailyBranch(rawValue: branchRaw) else {
                throw MusicServiceError.corruptStorage
            }
            return .daily(branch: branch)
        case "heartbeat":
            if let songID = try c.optionalText(46) {
                return .heartbeatSong(songID: songID)
            }
            return .heartbeat
        case "song-collection":
            return .songCollection(ids: lists["actionIds"] ?? [])
        default:
            throw MusicServiceError.corruptStorage
        }
    }
}
