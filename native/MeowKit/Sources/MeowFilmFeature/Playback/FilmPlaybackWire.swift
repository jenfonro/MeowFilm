import Foundation
import MeowRuntime

public enum FilmPlaybackKind: String, Sendable { case movie, tv, source, group }
public enum FilmMetadataProvider: String, Sendable {
    case tmdb, douban
    public var title: String { self == .tmdb ? "TMDB" : "豆瓣" }
}

/// Provider identifiers preserve their wire prototype, including numeric zero.
/// Display text must not be round-tripped back into a playback/browse identity.
public enum FilmPlaybackIdentifier: Equatable, Sendable {
    case string(String)
    case number(Int64)
    init(_ value: RuntimeValue) throws {
        switch value {
        case .string(let text): self = .string(text)
        case .number(let number) where number.isFinite &&
            abs(number) <= 9_007_199_254_740_991 && number.rounded(.towardZero) == number:
            self = .number(Int64(number))
        default: throw FilmPlaybackError.invalidResponse
        }
    }
    var value: RuntimeValue {
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
    public static func == (lhs: Self, rhs: Self) -> Bool {
        switch (lhs, rhs) {
        case (.string(let a), .string(let b)): a.utf8.elementsEqual(b.utf8)
        case (.number(let a), .number(let b)): a == b
        default: false
        }
    }
}

/// Carries the selected identity, not a title-only request or a reconstructed search.
public struct FilmPlaybackTarget: Equatable, Sendable {
    public let value: RuntimeObject
    public var id: String { value["id"]!.filmText! }
    public var title: String { value["title"]!.filmText! }
    public var kind: FilmPlaybackKind { FilmPlaybackKind(rawValue: value["kind"]!.filmText!)! }
    public var metadataProvider: FilmMetadataProvider? {
        value["metadataProvider"]?.filmText.flatMap(FilmMetadataProvider.init(rawValue:))
    }
    public var position: FilmEpisodePosition {
        .init(season: value["season"]?.filmInteger, episode: value["episode"]?.filmInteger,
              globalEpisode: value["globalEpisode"]?.filmInteger)
    }

    public init(id: String, kind: FilmPlaybackKind, title: String, year: Int? = nil,
                metadataProvider: FilmMetadataProvider? = nil, metadataID: String? = nil,
                position: FilmEpisodePosition = .init(), source: RuntimeObject? = nil,
                members: [RuntimeValue]? = nil) {
        var value: RuntimeObject = ["id": .string(id), "kind": .string(kind.rawValue), "title": .string(title)]
        value["year"] = year.map { .number(Double($0)) }
        value["metadataProvider"] = metadataProvider.map { .string($0.rawValue) }
        value["metadataId"] = metadataID.map(RuntimeValue.string)
        for (key, field) in position.value { value[key] = field }
        value["source"] = source.map(RuntimeValue.object)
        value["members"] = members.map(RuntimeValue.array)
        self.value = value
    }

    init(_ value: RuntimeValue) throws {
        let object = try value.filmRequiredObject()
        _ = try object.filmString("id"); _ = try object.filmString("title")
        guard FilmPlaybackKind(rawValue: try object.filmString("kind")) != nil else {
            throw FilmPlaybackError.invalidResponse
        }
        self.value = object
    }
}

public struct FilmEpisodePosition: Equatable, Sendable {
    public let season: Int?
    public let episode: Int?
    public let globalEpisode: Int?
    public init(season: Int? = nil, episode: Int? = nil, globalEpisode: Int? = nil) {
        self.season = season; self.episode = episode; self.globalEpisode = globalEpisode
    }
    public var value: RuntimeObject {
        var fields = RuntimeObject()
        fields["season"] = season.map { .number(Double($0)) }
        fields["episode"] = episode.map { .number(Double($0)) }
        fields["globalEpisode"] = globalEpisode.map { .number(Double($0)) }
        return fields
    }
}

public struct FilmPlaybackSource: Equatable, Sendable {
    public let value: RuntimeObject
    public var key: String? { value["key"]?.filmText }
    public var name: String { value["name"]?.filmText ?? "" }
    public var detailID: RuntimeValue? { value["detailId"] }

    public init(key: String? = nil, engine: String, installationID: String, siteID: String,
                revision: Int64? = nil, generation: Int64, name: String,
                enabled: Bool = true, searchable: Bool = true) {
        var value: RuntimeObject = [
            "engine": .string(engine), "installationId": .string(installationID),
            "siteId": .string(siteID), "generation": .number(Double(generation)), "name": .string(name),
            "enabled": .bool(enabled), "searchable": .bool(searchable),
        ]
        value["key"] = key.map(RuntimeValue.string)
        value["revision"] = revision.map { .number(Double($0)) }
        self.value = value
    }

    init(_ value: RuntimeValue) throws {
        let object = try value.filmRequiredObject()
        _ = try object.filmString("key")
        _ = try object.filmString("name")
        let engine = try object.filmString("engine")
        if engine == "cat" || engine == "source" {
            _ = try object.filmString("installationId")
            _ = try object.filmString("siteId")
        } else if engine != "pan" {
            throw FilmPlaybackError.invalidResponse
        }
        if let detailID = object["detailId"] { _ = try FilmPlaybackIdentifier(detailID) }
        // A manual share is a pan resource, not an installed script. Its
        // provider is candidate.provider; a display name is never a pan alias.
        self.value = object
    }
}

public struct FilmPlaybackRequest: Sendable {
    public let target: FilmPlaybackTarget
    public let sources: [FilmPlaybackSource]
    public let query: String?
    public let results: [RuntimeValue]
    public let resultHints: [RuntimeValue]
    public let search: [RuntimeValue]
    public let metadata: RuntimeValue?
    public let cacheEntries: [RuntimeValue]
    public let rules: RuntimeValue?

    public init(target: FilmPlaybackTarget, sources: [FilmPlaybackSource], query: String? = nil,
                results: [RuntimeValue] = [], resultHints: [RuntimeValue] = [], search: [RuntimeValue] = [],
                metadata: RuntimeValue? = nil, cacheEntries: [RuntimeValue] = [], rules: RuntimeValue? = nil) {
        self.target = target; self.sources = sources; self.query = query
        self.results = results; self.resultHints = resultHints; self.search = search; self.metadata = metadata
        self.cacheEntries = cacheEntries; self.rules = rules
    }

    var value: RuntimeValue {
        var fields: RuntimeObject = [
            "platform": .string("ios"), "target": .object(target.value),
            "sources": .array(sources.map { .object($0.value) }),
            "results": .array(results), "resultHints": .array(resultHints), "search": .array(search),
            "cacheEntries": .array(cacheEntries),
        ]
        fields["query"] = query.map(RuntimeValue.string)
        fields["metadata"] = metadata
        fields["rules"] = rules
        return .object(fields)
    }

    func retargeted(_ target: FilmPlaybackTarget) -> Self {
        let sameMetadata = target.metadataProvider == self.target.metadataProvider &&
            target.value["metadataId"] == self.target.value["metadataId"]
        return .init(target: target, sources: sources, query: query, results: results, resultHints: resultHints,
                     search: search, metadata: sameMetadata ? metadata : nil, cacheEntries: cacheEntries, rules: rules)
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
    case hdr = "4k_hdr", highFrameRate = "4k_fps", uhd = "4k", fullHD = "1080p", hd = "720p"
    public var title: String {
        switch self {
        case .hdr: "4K HDR"
        case .highFrameRate: "4K 高帧率"
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

    public static let defaults = Self(ruleOrder: [.quality, .pan, .keyword],
        panOrder: [.yidong, .tianyi, .quark, .uc, .baidu], keywords: [],
        siteCleanKeywords: ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], iosAutoPrepare: true)

    public init(ruleOrder: [FilmPreferenceDimension], panOrder: [FilmPanProvider],
                keywords: [String], siteCleanKeywords: [String], iosAutoPrepare: Bool) {
        self.ruleOrder = ruleOrder; self.panOrder = panOrder; self.keywords = keywords
        self.siteCleanKeywords = siteCleanKeywords; self.iosAutoPrepare = iosAutoPrepare
    }

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        ruleOrder = try fields.filmArray("ruleOrder").map {
            guard let text = $0.filmText, let dimension = FilmPreferenceDimension(rawValue: text) else {
                throw FilmPlaybackError.invalidResponse
            }
            return dimension
        }
        panOrder = try fields.filmArray("panOrder").map {
            guard let text = $0.filmText, let provider = FilmPanProvider(rawValue: text) else {
                throw FilmPlaybackError.invalidResponse
            }
            return provider
        }
        keywords = try fields.filmStrings("keywords")
        siteCleanKeywords = try fields.filmStrings("siteCleanKeywords")
        iosAutoPrepare = try fields.filmBool("iosAutoPrepare")
    }

    var value: RuntimeValue {
        .object([
            "ruleOrder": .array(ruleOrder.map { .string($0.rawValue) }),
            "panOrder": .array(panOrder.map { .string($0.rawValue) }),
            "keywords": .array(keywords.map(RuntimeValue.string)),
            "siteCleanKeywords": .array(siteCleanKeywords.map(RuntimeValue.string)),
            "iosAutoPrepare": .bool(iosAutoPrepare),
        ])
    }
}

public struct FilmPlaybackCandidate: Equatable, Sendable {
    public let id: String
    public let source: FilmPlaybackSource
    public let lineID: FilmPlaybackIdentifier
    public let lineLabel: String
    /// Unknown labels stay labels. Only this explicit field classifies a pan family.
    public let provider: String?
    public let name: String
    public let path: String?
    public let position: FilmEpisodePosition
    public let quality: String?

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id")
        source = try FilmPlaybackSource(fields.filmValue("source"))
        lineID = try FilmPlaybackIdentifier(fields.filmValue("lineId"))
        lineLabel = try fields.filmString("lineLabel")
        provider = fields["provider"]?.filmText
        name = try fields.filmString("name"); path = fields["path"]?.filmText
        quality = fields["quality"]?.filmText
        position = .init(season: fields["season"]?.filmInteger, episode: fields["episode"]?.filmInteger,
                         globalEpisode: fields["globalEpisode"]?.filmInteger)
    }
}

public struct FilmPlaybackSourceRow: Equatable, Sendable {
    public let id: String
    public let title: String
    public let source: FilmPlaybackSource
    public let state: String
    public let error: String?
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id"); title = try fields.filmString("title")
        source = try FilmPlaybackSource(fields.filmValue("source"))
        state = try fields.filmString("state"); error = fields["error"]?.filmText
    }
}

public struct FilmPlaybackMetadataSource: Equatable, Sendable {
    public let id: String
    public let provider: FilmMetadataProvider
    public let metadataID: FilmPlaybackIdentifier
    public let kind: FilmPlaybackKind
    public let title: String
    public let seasons: [RuntimeValue]?

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id")
        guard let provider = FilmMetadataProvider(rawValue: try fields.filmString("provider")),
              let kind = FilmPlaybackKind(rawValue: try fields.filmString("kind")) else {
            throw FilmPlaybackError.invalidResponse
        }
        self.provider = provider; self.kind = kind
        metadataID = try FilmPlaybackIdentifier(fields.filmValue("metadataId"))
        title = try fields.filmString("title")
        if let value = fields["seasons"], value != .null {
            guard let seasons = value.filmArray else { throw FilmPlaybackError.invalidResponse }
            self.seasons = seasons
        } else { seasons = nil }
    }
}

public struct FilmPlaybackLine: Equatable, Sendable {
    public let id: FilmPlaybackIdentifier
    public let label: String
    public let provider: String?
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try FilmPlaybackIdentifier(fields.filmValue("id")); label = try fields.filmString("label")
        provider = fields["provider"]?.filmText
    }
}

public struct FilmPlaybackFile: Equatable, Sendable {
    public let id: String
    public let candidateID: String?
    public let name: String
    public let path: String?
    public let position: FilmEpisodePosition
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id"); name = try fields.filmString("name")
        candidateID = fields["candidateId"]?.filmText; path = fields["path"]?.filmText
        position = .init(season: fields["season"]?.filmInteger, episode: fields["episode"]?.filmInteger,
                         globalEpisode: fields["globalEpisode"]?.filmInteger)
    }
}

public struct FilmPlaybackBrowsing: Equatable, Sendable {
    public let sourceID: String?
    public let lineID: FilmPlaybackIdentifier?
    public let season: Int?
    public let loading: Bool
    public let error: String?
    public let lines: [FilmPlaybackLine]
    public let files: [FilmPlaybackFile]
    public let originalFiles: [FilmPlaybackFile]
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        sourceID = fields["sourceId"]?.filmText
        lineID = try fields.filmOptional("lineId").map(FilmPlaybackIdentifier.init)
        season = fields["season"]?.filmInteger
        loading = try fields.filmBool("loading"); error = fields["error"]?.filmText
        lines = try fields.filmArray("lines").map(FilmPlaybackLine.init)
        let files = try fields.filmArray("episodes").map(FilmPlaybackFile.init)
        // A failed current list must never expose a previous list's playable rows.
        self.files = error == nil ? files : []
        let original: [FilmPlaybackFile] = try fields["originalEpisodes"].map { value in
            guard let values = value.filmArray else { throw FilmPlaybackError.invalidResponse }
            return try values.map(FilmPlaybackFile.init)
        } ?? []
        originalFiles = error == nil ? original : []
    }
}

public struct FilmPlaybackEpisode: Equatable, Sendable {
    public let raw: RuntimeObject
    public let position: FilmEpisodePosition
    public let name: String?
    public var displayID: String {
        "season-\(position.season!)-episode-\(position.episode!)"
    }
    init(_ value: RuntimeValue) throws {
        raw = try value.filmRequiredObject()
        guard let season = raw["season"]?.filmInteger, let episode = raw["episode"]?.filmInteger else {
            throw FilmPlaybackError.invalidResponse
        }
        position = .init(season: season, episode: episode, globalEpisode: raw["globalEpisode"]?.filmInteger)
        name = raw["name"]?.filmText
    }
}

public struct FilmPlaybackAttempt: Equatable, Sendable {
    public let id: String
    public let candidate: FilmPlaybackCandidate
    public let prepareOnly: Bool
    public let position: Double
    public let paused: Bool
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id"); candidate = try FilmPlaybackCandidate(fields.filmValue("candidate"))
        prepareOnly = try fields.filmBool("prepareOnly")
        position = try fields.filmNumber("position"); paused = try fields.filmBool("paused")
    }
}

public struct FilmPlaybackCurrent: Equatable, Sendable {
    public let attemptID: String
    public let candidate: FilmPlaybackCandidate
    public let position: Double
    public let duration: Double
    public let paused: Bool
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        attemptID = try fields.filmString("attemptId")
        candidate = try FilmPlaybackCandidate(fields.filmValue("candidate"))
        position = try fields.filmNumber("position"); duration = try fields.filmNumber("duration")
        paused = try fields.filmBool("paused")
    }
}

public struct FilmPlaybackSnapshot: Equatable, Sendable {
    public let sessionID: UUID
    public let sequence: Int64
    public let target: FilmPlaybackTarget
    public let stage: String
    public let error: String?
    public let metadata: RuntimeValue
    public let metadataSources: [FilmPlaybackMetadataSource]
    public let metadataSourceID: String?
    public let metadataError: String?
    public let sources: [FilmPlaybackSourceRow]
    public let browsing: FilmPlaybackBrowsing
    public let episodes: [FilmPlaybackEpisode]
    public let candidates: [FilmPlaybackCandidate]
    public let attempt: FilmPlaybackAttempt?
    public let current: FilmPlaybackCurrent?
    public let searchComplete: Bool
    public let preferences: FilmPlaybackPreferences
    public let shortVideo: Bool
    public let previewEpisodes: Bool
    public let reportError: String?
    public let prefetch: RuntimeValue?

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let id = UUID(uuidString: try fields.filmString("sessionId")),
              let sequence = fields["sequence"]?.filmInteger else { throw FilmPlaybackError.invalidResponse }
        sessionID = id; self.sequence = Int64(sequence)
        target = try FilmPlaybackTarget(fields.filmValue("target"))
        stage = try fields.filmString("stage"); error = fields["error"]?.filmText
        metadata = try fields.filmValue("metadata")
        metadataSources = try fields["metadataSources"].map { value in
            guard let values = value.filmArray else { throw FilmPlaybackError.invalidResponse }
            return try values.map(FilmPlaybackMetadataSource.init)
        } ?? []
        metadataSourceID = fields["metadataSourceId"]?.filmText
        metadataError = fields["metadataError"]?.filmText
        sources = try fields.filmArray("sources").map(FilmPlaybackSourceRow.init)
        browsing = try FilmPlaybackBrowsing(fields.filmValue("browsing"))
        episodes = try fields.filmArray("episodes").map(FilmPlaybackEpisode.init)
        candidates = try fields.filmArray("candidates").map(FilmPlaybackCandidate.init)
        attempt = try fields.filmOptional("attempt").map(FilmPlaybackAttempt.init)
        current = try fields.filmOptional("current").map(FilmPlaybackCurrent.init)
        searchComplete = try fields.filmBool("searchComplete")
        preferences = try FilmPlaybackPreferences(fields.filmValue("preferences"))
        shortVideo = try fields.filmBool("shortVideo"); previewEpisodes = try fields.filmBool("previewEpisodes")
        reportError = fields["reportError"]?.filmText; prefetch = fields["prefetch"]
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
    public let scope: RuntimeValue

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
        scope = try fields.filmValue("scope")
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
        case "NO_MATCH", "MEDIA_UNAVAILABLE": "暂无可用片源"
        case "TIMEOUT": "请求超时"
        case "CLOSED", "HOST_CLOSED": "本地运行器已关闭"
        case "OBSERVATION_CLOSED": "播放状态连接已断开"
        case "INVALID_RESPONSE", "UPSTREAM_FORMAT": "返回数据格式错误"
        case "STORAGE_ERROR", "LIBRARY_UPDATE_FAILED": "保存失败"
        case "CORRUPT_STORAGE": "本地播放资料已损坏"
        case "BUSY": "操作尚未完成"
        case "EXTERNAL_PLAYER_NO_CURRENT": "当前没有可交接的播放"
        case "EXTERNAL_PLAYER_DRM": "此媒体需要额外解密信息，请继续内置播放"
        case "EXTERNAL_PLAYER_OPEN_FAILED": "无法打开所选播放器"
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
    func filmStrings(_ key: String) throws -> [String] {
        try filmArray(key).map {
            guard let value = $0.filmText else { throw FilmPlaybackError.invalidResponse }
            return value
        }
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
