import Foundation
import MeowDebug
import MeowRuntime

public enum FilmPlaybackKind: String, Codable, Sendable { case movie, tv }

public enum FilmMetadataProvider: String, Codable, Sendable {
    case tmdb, douban
    public var title: String { self == .tmdb ? "TMDB" : "豆瓣" }
}

/// Provider identifiers preserve their wire prototype, including numeric zero.
public enum FilmPlaybackIdentifier: Hashable, Codable, Sendable {
    case string(String)
    case number(Int64)

    public init(_ value: RuntimeValue) throws {
        switch value {
        case .string(let text) where !text.isEmpty:
            self = .string(text)
        case .number(let number) where number.isFinite &&
            abs(number) <= 9_007_199_254_740_991 && number.rounded(.towardZero) == number:
            self = .number(Int64(number))
        default:
            throw FilmPlaybackError.invalidResponse
        }
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let text = try? container.decode(String.self) {
            self = .string(text)
        } else {
            self = .number(try container.decode(Int64.self))
        }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let text): try container.encode(text)
        case .number(let num): try container.encode(num)
        }
    }

    public var value: RuntimeValue {
        switch self {
        case .string(let text): .string(text)
        case .number(let number): .number(Double(number))
        }
    }

    public var display: String {
        switch self {
        case .string(let text): text
        case .number(let number): String(number)
        }
    }
}

public struct FilmTmdbRef: Hashable, Codable, Sendable {
    public let kind: String
    public let id: Int
    public init(kind: String, id: Int) { self.kind = kind; self.id = id }
}

public struct FilmDoubanRef: Hashable, Codable, Sendable {
    public let kind: String
    public let id: String
    public init(kind: String, id: String) { self.kind = kind; self.id = id }
}

public struct FilmSiteEntryRef: Hashable, Codable, Sendable {
    public let sourceKey: String
    public let installationId: String?
    public let siteKey: String?
    public let detailId: FilmPlaybackIdentifier
    public let rawTitle: String
    public let remark: String
    public let poster: String

    public init(sourceKey: String, installationId: String? = nil, siteKey: String? = nil,
                detailId: FilmPlaybackIdentifier, rawTitle: String = "", remark: String = "", poster: String = "") {
        self.sourceKey = sourceKey
        self.installationId = installationId
        self.siteKey = siteKey
        self.detailId = detailId
        self.rawTitle = rawTitle
        self.remark = remark
        self.poster = poster
    }
}

/// Unified lightweight card entry shared across Home, Category, Search, Favorites, and History.
public struct FilmEntry: Hashable, Codable, Sendable {
    public let targetId: String
    public let keyword: String
    public let title: String
    public let subtitle: String
    public let poster: String
    public let backdrop: String
    public let score: String
    public let textBadge: String
    public let siteLabel: String
    public let kind: FilmItemKind?
    public let year: Int?
    public let season: Int?
    public let episode: Int?
    public let seasonHint: Int?
    public let tmdb: FilmTmdbRef?
    public let douban: FilmDoubanRef?
    public let siteEntry: FilmSiteEntryRef?

    public init(targetId: String, keyword: String, title: String, subtitle: String = "",
                poster: String = "", backdrop: String = "", score: String = "",
                textBadge: String = "", siteLabel: String = "", kind: FilmItemKind? = nil,
                year: Int? = nil, season: Int? = nil, episode: Int? = nil, seasonHint: Int? = nil,
                tmdb: FilmTmdbRef? = nil, douban: FilmDoubanRef? = nil, siteEntry: FilmSiteEntryRef? = nil) {
        self.targetId = targetId
        self.keyword = keyword
        self.title = title
        self.subtitle = subtitle
        self.poster = poster
        self.backdrop = backdrop
        self.score = score
        self.textBadge = textBadge
        self.siteLabel = siteLabel
        self.kind = kind
        self.year = year
        self.season = season
        self.episode = episode
        self.seasonHint = seasonHint
        self.tmdb = tmdb
        self.douban = douban
        self.siteEntry = siteEntry
    }

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        let titleText = fields["title"]?.filmRuleDisplayText ?? ""
        let keywordText = fields["keyword"]?.filmRuleDisplayText ?? titleText
        let targetText = fields["targetId"]?.filmRuleDisplayText ?? ""
        guard !titleText.isEmpty, !targetText.isEmpty else { throw FilmPlaybackError.invalidResponse }

        let parsedKind = fields["kind"]?.filmText.flatMap(FilmItemKind.init(rawValue:))

        var parsedTmdb: FilmTmdbRef?
        if let tmdbObj = fields["tmdb"]?.filmObject,
           let tk = tmdbObj["kind"]?.filmText, (tk == "movie" || tk == "tv"),
           let tid = tmdbObj["id"]?.filmInteger, tid > 0 {
            parsedTmdb = FilmTmdbRef(kind: tk, id: tid)
        }

        var parsedDouban: FilmDoubanRef?
        if let doubanObj = fields["douban"]?.filmObject,
           let dk = doubanObj["kind"]?.filmText, (dk == "movie" || dk == "tv"),
           let did = doubanObj["id"]?.filmScalar, !did.isEmpty {
            parsedDouban = FilmDoubanRef(kind: dk, id: did)
        }

        var parsedSite: FilmSiteEntryRef?
        if let siteObj = fields["siteEntry"]?.filmObject,
           let sourceKey = siteObj["sourceKey"]?.filmText, !sourceKey.isEmpty,
           let detailRaw = siteObj["detailId"], let detailId = try? FilmPlaybackIdentifier(detailRaw) {
            parsedSite = FilmSiteEntryRef(
                sourceKey: sourceKey,
                installationId: siteObj["installationId"]?.filmText.flatMap { $0.isEmpty ? nil : $0 },
                siteKey: siteObj["siteKey"]?.filmText.flatMap { $0.isEmpty ? nil : $0 },
                detailId: detailId,
                rawTitle: siteObj["rawTitle"]?.filmRuleDisplayText ?? "",
                remark: siteObj["remark"]?.filmRuleDisplayText ?? "",
                poster: siteObj["poster"]?.filmRuleDisplayText ?? ""
            )
        }

        self.init(
            targetId: targetText,
            keyword: keywordText.isEmpty ? titleText : keywordText,
            title: titleText,
            subtitle: fields["subtitle"]?.filmRuleDisplayText ?? "",
            poster: fields["poster"]?.filmRuleDisplayText ?? "",
            backdrop: fields["backdrop"]?.filmRuleDisplayText ?? "",
            score: fields["score"]?.filmRuleDisplayText ?? "",
            textBadge: fields["textBadge"]?.filmRuleDisplayText ?? "",
            siteLabel: fields["siteLabel"]?.filmRuleDisplayText ?? "",
            kind: parsedKind,
            year: fields["year"]?.filmInteger,
            season: fields["season"]?.filmInteger,
            episode: fields["episode"]?.filmInteger,
            seasonHint: fields["seasonHint"]?.filmInteger,
            tmdb: parsedTmdb,
            douban: parsedDouban,
            siteEntry: parsedSite
        )
    }

    public var wireValue: RuntimeValue {
        var fields: RuntimeObject = [
            "targetId": .string(targetId),
            "keyword": .string(keyword),
            "title": .string(title),
            "subtitle": .string(subtitle),
            "poster": .string(poster),
            "backdrop": .string(backdrop),
            "score": .string(score),
            "textBadge": .string(textBadge),
            "siteLabel": .string(siteLabel),
        ]
        if let kind { fields["kind"] = .string(kind.rawValue) }
        if let year { fields["year"] = .number(Double(year)) }
        if let season { fields["season"] = .number(Double(season)) }
        if let episode { fields["episode"] = .number(Double(episode)) }
        if let seasonHint { fields["seasonHint"] = .number(Double(seasonHint)) }
        if let tmdb {
            fields["tmdb"] = .object(["kind": .string(tmdb.kind), "id": .number(Double(tmdb.id))])
        }
        if let douban {
            fields["douban"] = .object(["kind": .string(douban.kind), "id": .string(douban.id)])
        }
        if let siteEntry {
            var siteObj: RuntimeObject = [
                "sourceKey": .string(siteEntry.sourceKey),
                "detailId": siteEntry.detailId.value,
                "rawTitle": .string(siteEntry.rawTitle),
                "remark": .string(siteEntry.remark),
                "poster": .string(siteEntry.poster),
            ]
            if let inst = siteEntry.installationId { siteObj["installationId"] = .string(inst) }
            if let sk = siteEntry.siteKey { siteObj["siteKey"] = .string(sk) }
            fields["siteEntry"] = .object(siteObj)
        }
        return .object(fields)
    }
}

public struct FilmPlaybackRequest: Sendable {
    public let entry: FilmEntry
    public let searchReference: RuntimeValue?
    public var restoreOnly: Bool

    public init(entry: FilmEntry, searchReference: RuntimeValue? = nil, restoreOnly: Bool = false) {
        self.entry = entry
        self.searchReference = searchReference
        self.restoreOnly = restoreOnly
    }

    var value: RuntimeValue {
        var fields: RuntimeObject = [
            "platform": .string("ios"),
            "entry": entry.wireValue,
        ]
        if let searchReference { fields["searchReference"] = searchReference }
        if restoreOnly { fields["restoreOnly"] = .bool(true) }
        return .object(fields)
    }
}

public enum FilmPanProvider: String, CaseIterable, Sendable {
    case yidong, tianyi, quark, uc, baidu
    public var title: String {
        switch self {
        case .yidong: "移动云盘"
        case .tianyi: "天翼云盘"
        case .quark: "夸克网盘"
        case .uc: "UC 网盘"
        case .baidu: "百度网盘"
        }
    }
}

public enum FilmResourceQuality: String, CaseIterable, Sendable {
    case uhd8K = "8k", hdr = "4k_hdr", highFrameRate = "4k_fps", uhd = "4k", fullHD = "1080p", hd = "720p"
    public var title: String {
        switch self {
        case .uhd8K: "8K"
        case .hdr: "4K HDR"
        case .highFrameRate: "4K 60帧"
        case .uhd: "4K"
        case .fullHD: "1080P"
        case .hd: "720P"
        }
    }
}

public enum FilmPreferenceDimension: String, CaseIterable, Sendable {
    case quality, pan, keyword
    public var title: String {
        switch self {
        case .quality: "画质"
        case .pan: "网盘"
        case .keyword: "关键词"
        }
    }
}

public struct FilmPlaybackPreferences: Equatable, Sendable {
    public var ruleOrder: [FilmPreferenceDimension]
    public var panOrder: [FilmPanProvider]
    public var keywords: [String]
    public var siteCleanKeywords: [String]
    public var iosAutoPrepare: Bool
    public var metadataViewProvider: FilmMetadataProvider

    public static let defaults = Self(ruleOrder: [.quality, .pan, .keyword],
        panOrder: [.yidong, .tianyi, .quark, .uc, .baidu], keywords: [],
        siteCleanKeywords: ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], iosAutoPrepare: true)

    public init(ruleOrder: [FilmPreferenceDimension], panOrder: [FilmPanProvider],
                keywords: [String], siteCleanKeywords: [String], iosAutoPrepare: Bool,
                metadataViewProvider: FilmMetadataProvider = .tmdb) {
        self.ruleOrder = ruleOrder; self.panOrder = panOrder; self.keywords = keywords
        self.siteCleanKeywords = siteCleanKeywords; self.iosAutoPrepare = iosAutoPrepare
        self.metadataViewProvider = metadataViewProvider
    }

    init(record: FilmPreferencesRecord) throws {
        let rules = record.ruleOrder.compactMap(FilmPreferenceDimension.init(rawValue:))
        let pans = record.panOrder.compactMap(FilmPanProvider.init(rawValue:))
        let provider = FilmMetadataProvider(rawValue: record.metadataViewProvider) ?? .tmdb
        self.init(
            ruleOrder: rules.isEmpty ? Self.defaults.ruleOrder : rules,
            panOrder: pans.isEmpty ? Self.defaults.panOrder : pans,
            keywords: record.keywords,
            siteCleanKeywords: record.siteCleanKeywords,
            iosAutoPrepare: record.iosAutoPrepare,
            metadataViewProvider: provider
        )
    }

    var record: FilmPreferencesRecord {
        FilmPreferencesRecord(
            iosAutoPrepare: iosAutoPrepare,
            metadataViewProvider: metadataViewProvider.rawValue,
            ruleOrder: ruleOrder.map(\.rawValue),
            panOrder: panOrder.map(\.rawValue),
            keywords: keywords,
            siteCleanKeywords: siteCleanKeywords
        )
    }
}

public struct FilmSelectionOption: Equatable, Sendable {
    public let id: String
    public let label: String
    public let kind: String
    public let subtitle: String
    public let season: Int?
    public let provider: String?
    public let state: String?
    public let error: String?

    public var displayTitle: String {
        subtitle.isEmpty ? label : "\(label) · \(subtitle)"
    }
}

public struct FilmWorkSnapshot: Equatable, Sendable {
    public let targetID: String
    public let keyword: String
    public let kind: FilmPlaybackKind
    public let title: String
    public let subtitle: String
    public let poster: String
    public let backdrop: String
    public let logo: String
    public let year: String
    public let countries: String
    public let duration: String
    public let seasonCount: Int?
    public let episodeCount: Int?
    public let genres: [String]
    public let score: String
    public let scoreProvider: String
    public let directors: String
    public let cast: [FilmCastMember]
    public let summary: String
    public let tmdb: FilmTmdbRef?
    public let douban: FilmDoubanRef?
    public let relatedItems: [FilmItem]

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        targetID = fields["targetId"]?.filmText ?? ""
        keyword = fields["keyword"]?.filmText ?? ""
        kind = fields["kind"]?.filmText == "movie" ? .movie : .tv
        title = fields["title"]?.filmText ?? ""
        subtitle = fields["subtitle"]?.filmText ?? ""
        poster = fields["poster"]?.filmText ?? ""
        backdrop = fields["backdrop"]?.filmText ?? ""
        logo = fields["logo"]?.filmText ?? ""
        year = fields["year"]?.filmText ?? ""
        countries = fields["countries"]?.filmText ?? ""
        duration = fields["duration"]?.filmText ?? ""
        seasonCount = fields["seasonCount"]?.filmInteger
        episodeCount = fields["episodeCount"]?.filmInteger
        genres = (fields["genres"]?.filmArray ?? []).compactMap { $0.filmText }.filter { !$0.isEmpty }
        score = fields["score"]?.filmText ?? ""
        scoreProvider = fields["scoreProvider"]?.filmText ?? ""
        directors = fields["directors"]?.filmText ?? ""
        summary = fields["summary"]?.filmText ?? ""

        if let tmdbObj = fields["tmdb"]?.filmObject,
           let tk = tmdbObj["kind"]?.filmText, (tk == "movie" || tk == "tv"),
           let tid = tmdbObj["id"]?.filmInteger, tid > 0 {
            tmdb = FilmTmdbRef(kind: tk, id: tid)
        } else {
            tmdb = nil
        }

        if let doubanObj = fields["douban"]?.filmObject,
           let dk = doubanObj["kind"]?.filmText, (dk == "movie" || dk == "tv"),
           let did = doubanObj["id"]?.filmScalar, !did.isEmpty {
            douban = FilmDoubanRef(kind: dk, id: did)
        } else {
            douban = nil
        }

        cast = (fields["cast"]?.filmArray ?? []).compactMap { raw -> FilmCastMember? in
            guard let row = raw.filmObject, let name = row["name"]?.filmText, !name.isEmpty else { return nil }
            let id = row["id"]?.filmScalar ?? name
            let role = row["role"]?.filmText ?? ""
            let avatarURL = FilmPlaybackPresentation.artwork(row["avatar"])
            return FilmCastMember(id: id, name: name, role: role, portraitURL: avatarURL)
        }

        relatedItems = (fields["relatedCards"]?.filmArray ?? []).compactMap { raw -> FilmItem? in
            guard let row = raw.filmObject,
                  let id = row["id"]?.filmText, !id.isEmpty,
                  let entry = try? FilmEntry(value: raw) else { return nil }
            return FilmItem(
                sourceID: entry.tmdb != nil ? "tmdb" : entry.douban != nil ? "douban" : "related",
                id: entry.tmdb.map { String($0.id) } ?? entry.douban?.id ?? id,
                title: entry.title,
                kind: entry.kind,
                posterURL: FilmPlaybackPresentation.artwork(.string(entry.poster)),
                score: entry.score,
                subtitle: entry.subtitle,
                entry: entry
            )
        }
    }
}

public struct FilmDirectoryItem: Equatable, Sendable {
    public let id: String
    public let index: Int
    public let season: Int?
    public let episode: Int?
    public let numberLabel: String
    public let title: String
    public let fileName: String
    public let path: String
    public let sourceLabel: String
    public let summary: String
    public let still: String
    public let duration: String
    public let airDate: String
    public let quality: String
    public let active: Bool

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id")
        index = fields["index"]?.filmInteger ?? 0
        season = fields["season"]?.filmInteger
        episode = fields["episode"]?.filmInteger
        numberLabel = fields["numberLabel"]?.filmText ?? ""
        title = fields["title"]?.filmRuleDisplayText ?? ""
        fileName = fields["fileName"]?.filmRuleDisplayText ?? ""
        path = fields["path"]?.filmRuleDisplayText ?? ""
        sourceLabel = fields["sourceLabel"]?.filmText ?? ""
        summary = fields["summary"]?.filmText ?? ""
        still = fields["still"]?.filmText ?? ""
        duration = fields["duration"]?.filmText ?? ""
        airDate = fields["airDate"]?.filmText ?? ""
        quality = fields["quality"]?.filmText ?? ""
        active = fields["active"] == .bool(true)
    }

    public var isPlayable: Bool { !id.hasPrefix("raw:") }
}

public struct FilmPlayerOption: Equatable, Sendable {
    public let key: String
    public let label: String
    public let active: Bool
}

public struct FilmAttemptInfo: Equatable, Sendable {
    public let id: String
    public let candidateID: String
    public let prepareOnly: Bool
    public let position: Double
    public let duration: Double
    public let paused: Bool
    public let title: String
    public let season: Int?
    public let episode: Int?
    public let globalEpisode: Int?
    public let episodeLabel: String
    public let sourceLabel: String
    public let quality: String
    public let provider: String?

    init?(_ value: RuntimeValue?, isCurrent: Bool) throws {
        guard let value, value != .null else { return nil }
        let fields = try value.filmRequiredObject()
        id = try fields.filmString(isCurrent ? "attemptId" : "id")
        candidateID = fields["candidateId"]?.filmText ?? ""
        prepareOnly = !isCurrent && (fields["prepareOnly"] == .bool(true))
        position = fields["position"]?.filmNumber ?? 0
        duration = isCurrent ? (fields["duration"]?.filmNumber ?? 0) : 0
        paused = fields["paused"] == .bool(true)
        title = fields["title"]?.filmText ?? ""
        season = fields["season"]?.filmInteger
        episode = fields["episode"]?.filmInteger
        globalEpisode = fields["globalEpisode"]?.filmInteger
        episodeLabel = fields["episodeLabel"]?.filmText ?? ""
        sourceLabel = fields["sourceLabel"]?.filmText ?? ""
        quality = fields["quality"]?.filmText ?? ""
        provider = fields["provider"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
    }
}

public struct FilmPlaybackSnapshot: Equatable, Sendable {
    public let sessionID: UUID
    public let sequence: Int64
    public let targetID: String
    public let keyword: String
    public let stage: String
    public let error: String?
    public let errorStage: String?
    public let reportError: String?
    public let noSources: Bool
    public let retryMediaAttemptID: String?
    public private(set) var work: FilmWorkSnapshot

    public private(set) var sourceSelectedID: String?
    public private(set) var sourcePlaceholder: String
    public private(set) var sourceDisabled: Bool
    public private(set) var sourceCanLoadMore: Bool
    public private(set) var sourceLoadingMore: Bool
    public private(set) var sourceOptions: [FilmSelectionOption]

    public private(set) var lineVisible: Bool
    public private(set) var lineSelectedID: String?
    public private(set) var lineOptions: [FilmSelectionOption]

    public private(set) var directoryMode: String
    public private(set) var directoryLoading: Bool
    public private(set) var directoryRefreshing: Bool
    public private(set) var directoryError: String?
    public private(set) var directorySelectedSeason: Int?
    public private(set) var directoryCanLoadMore: Bool
    public private(set) var directoryLoadingMore: Bool
    public private(set) var previewEpisodes: Bool
    public private(set) var canTogglePreview: Bool
    public private(set) var directorySeasons: [FilmSelectionOption]
    public private(set) var directoryItems: [FilmDirectoryItem]
    public private(set) var directoryRawItems: [FilmDirectoryItem]

    public let shortVideo: Bool
    public let canPrevious: Bool
    public let canNext: Bool
    public let canSwitchSource: Bool
    public let canBlockSource: Bool
    public let activeSourceLabel: String
    public let activeEpisodeLabel: String
    public let activeQuality: String
    public let activePan: String
    public let availableQualities: [FilmPlayerOption]
    public let availablePans: [FilmPlayerOption]
    public let attempt: FilmAttemptInfo?
    public let current: FilmAttemptInfo?

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let id = UUID(uuidString: try fields.filmString("sessionId")),
              let sequence = fields["sequence"]?.filmInteger else {
            throw FilmPlaybackError.invalidResponse
        }
        sessionID = id
        self.sequence = Int64(sequence)
        targetID = try fields.filmString("targetId")
        keyword = fields["keyword"]?.filmText ?? ""
        stage = try fields.filmString("stage")
        error = fields["error"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        errorStage = fields["errorStage"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        reportError = fields["reportError"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        noSources = fields["noSources"] == .bool(true)
        retryMediaAttemptID = fields["retryMediaAttemptId"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        work = try FilmWorkSnapshot(fields.filmValue("work"))

        let sm = try fields.filmValue("sourceMenu").filmRequiredObject()
        sourceSelectedID = sm["selectedId"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        sourcePlaceholder = sm["placeholder"]?.filmText.flatMap { $0.isEmpty ? nil : $0 } ?? "选择站点源"
        sourceDisabled = sm["disabled"] == .bool(true)
        sourceCanLoadMore = sm["canLoadMore"] == .bool(true)
        sourceLoadingMore = sm["loadingMore"] == .bool(true)
        sourceOptions = try sm.filmArray("options").map { raw in
            let opt = try raw.filmRequiredObject()
            return FilmSelectionOption(
                id: try opt.filmString("id"),
                label: opt["label"]?.filmText ?? "",
                kind: opt["kind"]?.filmText ?? "",
                subtitle: opt["subtitle"]?.filmText ?? "",
                season: nil,
                provider: nil,
                state: opt["state"]?.filmText,
                error: opt["error"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
            )
        }

        let lm = try fields.filmValue("lineMenu").filmRequiredObject()
        lineVisible = lm["visible"] == .bool(true)
        lineSelectedID = lm["selectedId"]?.filmScalar.flatMap { $0.isEmpty ? nil : $0 }
        lineOptions = try lm.filmArray("options").map { raw in
            let opt = try raw.filmRequiredObject()
            return FilmSelectionOption(
                id: try opt.filmString("id"),
                label: opt["label"]?.filmText ?? "",
                kind: "line",
                subtitle: "",
                season: nil,
                provider: opt["provider"]?.filmText.flatMap { $0.isEmpty ? nil : $0 },
                state: nil,
                error: nil
            )
        }

        let dir = try fields.filmValue("directory").filmRequiredObject()
        directoryMode = dir["mode"]?.filmText.flatMap { $0.isEmpty ? nil : $0 } ?? "empty"
        directoryLoading = dir["loading"] == .bool(true)
        directoryRefreshing = dir["refreshing"] == .bool(true)
        directoryError = dir["error"]?.filmText.flatMap { $0.isEmpty ? nil : $0 }
        directorySelectedSeason = dir["selectedSeason"]?.filmInteger
        directoryCanLoadMore = dir["canLoadMore"] == .bool(true)
        directoryLoadingMore = dir["loadingMore"] == .bool(true)
        previewEpisodes = dir["previewEpisodes"] == .bool(true)
        canTogglePreview = dir["canTogglePreview"] == .bool(true)
        directorySeasons = try dir.filmArray("seasons").map { raw in
            let s = try raw.filmRequiredObject()
            let num = s["season"]?.filmInteger ?? 1
            return FilmSelectionOption(
                id: "season:\(num)",
                label: s["label"]?.filmText.flatMap { $0.isEmpty ? nil : $0 } ?? "第\(num)季",
                kind: "season",
                subtitle: "",
                season: num,
                provider: nil,
                state: nil,
                error: nil
            )
        }
        directoryItems = try dir.filmArray("items").map(FilmDirectoryItem.init)
        directoryRawItems = try (dir["rawItems"]?.filmArray ?? []).map(FilmDirectoryItem.init)

        let pl = try fields.filmValue("player").filmRequiredObject()
        shortVideo = pl["shortVideo"] == .bool(true)
        canPrevious = pl["canPrevious"] == .bool(true)
        canNext = pl["canNext"] == .bool(true)
        canSwitchSource = pl["canSwitchSource"] == .bool(true)
        canBlockSource = pl["canBlockSource"] == .bool(true)
        activeSourceLabel = pl["activeSourceLabel"]?.filmText ?? ""
        activeEpisodeLabel = pl["activeEpisodeLabel"]?.filmText ?? ""
        activeQuality = pl["activeQuality"]?.filmText ?? ""
        activePan = pl["activePan"]?.filmText ?? ""
        availableQualities = try (pl["availableQualities"]?.filmArray ?? []).map { raw in
            let q = try raw.filmRequiredObject()
            return FilmPlayerOption(
                key: try q.filmString("key"),
                label: q["label"]?.filmText ?? "",
                active: q["active"] == .bool(true)
            )
        }
        availablePans = try (pl["availablePans"]?.filmArray ?? []).map { raw in
            let p = try raw.filmRequiredObject()
            return FilmPlayerOption(
                key: try p.filmString("provider"),
                label: p["label"]?.filmText ?? "",
                active: p["active"] == .bool(true)
            )
        }
        attempt = try FilmAttemptInfo(pl["attempt"], isCurrent: false)
        current = try FilmAttemptInfo(pl["current"], isCurrent: true)
    }

    func retainingLoadedPresentation(_ previous: Self?) -> Self {
        guard let previous else { return self }
        var result = self
        if directoryLoading && directoryItems.isEmpty &&
           sourceSelectedID == previous.sourceSelectedID &&
           lineSelectedID == previous.lineSelectedID &&
           directorySelectedSeason == previous.directorySelectedSeason {
            result.directoryItems = previous.directoryItems
            result.directoryRawItems = previous.directoryRawItems
            result.directorySeasons = previous.directorySeasons
        }
        return result
    }

    /// True when only high-frequency playback telemetry (`sequence` or `current` position/duration/paused) changed.
    func hasSamePresentation(as other: Self) -> Bool {
        sessionID == other.sessionID &&
        targetID == other.targetID &&
        keyword == other.keyword &&
        stage == other.stage &&
        error == other.error &&
        errorStage == other.errorStage &&
        reportError == other.reportError &&
        noSources == other.noSources &&
        retryMediaAttemptID == other.retryMediaAttemptID &&
        work == other.work &&
        sourceSelectedID == other.sourceSelectedID &&
        sourcePlaceholder == other.sourcePlaceholder &&
        sourceDisabled == other.sourceDisabled &&
        sourceCanLoadMore == other.sourceCanLoadMore &&
        sourceLoadingMore == other.sourceLoadingMore &&
        sourceOptions == other.sourceOptions &&
        lineVisible == other.lineVisible &&
        lineSelectedID == other.lineSelectedID &&
        lineOptions == other.lineOptions &&
        directoryMode == other.directoryMode &&
        directoryLoading == other.directoryLoading &&
        directoryRefreshing == other.directoryRefreshing &&
        directoryError == other.directoryError &&
        directorySelectedSeason == other.directorySelectedSeason &&
        directoryCanLoadMore == other.directoryCanLoadMore &&
        directoryLoadingMore == other.directoryLoadingMore &&
        previewEpisodes == other.previewEpisodes &&
        canTogglePreview == other.canTogglePreview &&
        directorySeasons == other.directorySeasons &&
        directoryItems == other.directoryItems &&
        directoryRawItems == other.directoryRawItems &&
        shortVideo == other.shortVideo &&
        canPrevious == other.canPrevious &&
        canNext == other.canNext &&
        canSwitchSource == other.canSwitchSource &&
        canBlockSource == other.canBlockSource &&
        activeSourceLabel == other.activeSourceLabel &&
        activeEpisodeLabel == other.activeEpisodeLabel &&
        activeQuality == other.activeQuality &&
        activePan == other.activePan &&
        availableQualities == other.availableQualities &&
        availablePans == other.availablePans &&
        attempt == other.attempt &&
        current?.id == other.current?.id &&
        current?.candidateID == other.current?.candidateID
    }
}

/// Sensitive input: consumed by the native media owner, never ordinary presentation.
public struct FilmPlaybackMediaInput: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let sessionID: UUID
    public let attemptID: String
    public let resourceID: String
    public let url: URL
    public let format: String?
    public let drm: RuntimeValue?
    public let title: String
    public let position: Double
    public let paused: Bool

    init(value: RuntimeValue, sessionID: UUID, attemptID: String) throws {
        let fields = try value.filmRequiredObject()
        resourceID = try fields.filmString("id")
        guard !resourceID.isEmpty, try fields.filmString("attemptId") == attemptID,
              let url = URL(string: try fields.filmString("url")),
              ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.isEmpty == false else { throw FilmPlaybackError.invalidResponse }
        self.sessionID = sessionID; self.attemptID = attemptID; self.url = url
        format = fields["format"]?.filmText
        drm = fields["drm"]; title = try fields.filmString("title")
        position = try fields.filmNumber("position"); paused = try fields.filmBool("paused")
    }

    public var description: String { "FilmPlaybackMediaInput(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["attemptID": attemptID]) }
}

public struct FilmPlaybackError: Error, Equatable, Sendable, LocalizedError {
    public let code: String
    public init(code: String) { self.code = code }
    static let invalidResponse = Self(code: "INVALID_RESPONSE")
    static let closed = Self(code: "CLOSED")
    public var errorDescription: String? {
        switch code {
        case "NO_VIDEO_SOURCES": "暂无可用视频源"
        case "NO_MATCH", "MEDIA_UNAVAILABLE": "暂无可用片源"
        case "NO_EPISODE_MAPPING": "未找到对应集数"
        case "TIMEOUT": "请求超时"
        case "CLOSED", "HOST_CLOSED": "本地运行器已关闭"
        case "OBSERVATION_CLOSED": "播放状态连接已断开"
        case "INVALID_RESPONSE", "UPSTREAM_FORMAT": "返回数据格式错误"
        case "STORAGE_ERROR", "LIBRARY_UPDATE_FAILED": "保存失败"
        case "CORRUPT_STORAGE": "本地播放资料已损坏"
        case "NETWORK_ERROR", "MEDIA_HTTP_NETWORK_FAILED": "网络连接失败"
        case "NOT_CONFIGURED": "尚未完成数据源配置"
        case "AUTH_FAILED": "认证失败，请检查凭据"
        case "WEB_INTERACTION_REQUIRED": "需要完成网页验证，请在数据源设置中检查 Cookie 或代理后重试"
        case "MEDIA_HTTP_TLS_FAILED": "安全连接失败"
        case "UPSTREAM_HTTP", "UPSTREAM_ERROR", "MEDIA_UPSTREAM_ERROR": "上游服务请求失败"
        case "MEDIA_HTTP_DEADLINE", "MEDIA_UPSTREAM_TIMEOUT": "请求超时"
        case "MEDIA_HTTP_UNAUTHORIZED": "媒体认证失败"
        case "MEDIA_HTTP_NOT_FOUND", "MEDIA_NOT_FOUND": "媒体资源不存在"
        case "MEDIA_HTTP_EXPIRED": "媒体地址已过期"
        case "MEDIA_HTTP_THROTTLED": "媒体请求过于频繁"
        case "MEDIA_HTTP_TRUNCATED", "MEDIA_READ_TRUNCATED": "媒体响应不完整"
        case "MEDIA_RESOURCE_REVOKED", "MEDIA_READ_REVOKED": "媒体授权已失效"
        case "MEDIA_INPUT_INVALID", "MEDIA_INVALID_REQUEST": "媒体请求无效"
        case "MEDIA_FORMAT_UNSUPPORTED", "MEDIA_HTTP_FORMAT_UNSUPPORTED", "MEDIA_READ_FORMAT_UNSUPPORTED": "媒体格式不受支持"
        case "MEDIA_DECODING_FAILED": "媒体解码失败"
        case "MEDIA_OUTPUT_FAILED", "MEDIA_PLAYBACK_FAILED": "媒体播放失败"
        case "MEDIA_OPEN_FAILED": "媒体打开失败"
        case "MEDIA_READ_FAILED": "媒体读取失败"
        case "MEDIA_SEEK_FAILED", "MEDIA_READ_SEEK_FAILED": "媒体跳转失败"
        case "MEDIA_DECRYPTION_FAILED": "媒体解密失败"
        case "MEDIA_RELEASE_FAILED": "媒体关闭失败"
        case "SCRIPT_FAILURE", "CAT_FAILURE": "脚本执行失败"
        case "SCRIPT_PROTOCOL_ERROR": "脚本响应格式错误"
        case "SOURCE_FAILURE": "来源请求失败"
        case "PAN_INTERCEPTION_UNSUPPORTED": "网盘请求未能接管，请关闭对应网盘接管后重试"
        case "BUSY": "操作尚未完成"
        default: code
        }
    }
}

extension RuntimeValue {
    var filmText: String? { if case .string(let text) = self { text } else { nil } }
    var filmNumber: Double? { if case .number(let number) = self { number } else { nil } }
    var filmInteger: Int? {
        guard let number = filmNumber, number.isFinite, number >= 0,
              number <= 9_007_199_254_740_991, number.rounded(.towardZero) == number else { return nil }
        return Int(number)
    }
    var filmArray: [RuntimeValue]? { if case .array(let values) = self { values } else { nil } }
    var filmObject: RuntimeObject? { if case .object(let fields) = self { fields } else { nil } }
    var filmScalar: String? { filmText ?? filmNumber.map { String(format: "%.0f", $0) } }
    var filmRuleDisplayText: String? { filmText }
    func filmRequiredObject() throws -> RuntimeObject {
        guard let fields = filmObject else { throw FilmPlaybackError.invalidResponse }
        return fields
    }
}

extension RuntimeObject {
    func filmValue(_ key: String) throws -> RuntimeValue {
        guard let value = self[key] else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmString(_ key: String) throws -> String {
        guard let value = self[key]?.filmText else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmArray(_ key: String) throws -> [RuntimeValue] {
        guard let value = self[key]?.filmArray else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmBool(_ key: String) throws -> Bool {
        guard case .bool(let value)? = self[key] else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmNumber(_ key: String) throws -> Double {
        guard let value = self[key]?.filmNumber, value.isFinite, value >= 0 else {
            throw FilmPlaybackError.invalidResponse
        }
        return value
    }
    func filmOptional(_ key: String) throws -> RuntimeValue? {
        let value = try filmValue(key)
        return value == .null ? nil : value
    }
}
