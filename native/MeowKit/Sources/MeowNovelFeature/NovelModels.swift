import Foundation
import MeowRuntime

public enum NovelError: Error, Equatable, Sendable, LocalizedError {
    case closed, invalidResponse, corruptStorage, noSource, noChapter, selectionRequired, differentBook, imageReleaseFailed, invalidRule([String]), importFailures([String]), ruleExecution(String)
    public var errorDescription: String? {
        switch self {
        case .closed: return "小说会话已关闭"
        case .invalidResponse: return "书源返回的数据无效"
        case .corruptStorage: return "小说本地数据损坏，原文件已保留"
        case .noSource: return "没有可用书源"
        case .noChapter: return "没有可阅读章节"
        case .selectionRequired: return "请选择对应章节"
        case .differentBook: return "候选书籍的书名或作者不匹配"
        case .imageReleaseFailed: return "书源图片资源释放失败"
        case .invalidRule(let errors): return errors.joined(separator: "\n")
        case .importFailures(let errors): return errors.joined(separator: "\n")
        case .ruleExecution(let code): return code == "TIMEOUT" ? "小说规则执行超时" : "小说规则执行失败（\(code)）"
        }
    }
}

public struct NovelSource: Codable, Equatable, Sendable, Identifiable {
    public var installationId: String
    public var revision: Int
    public var name: String
    public var sourceUrl: String
    public var group: String
    public var order: Double
    public var enabled: Bool
    public var enabledExplore: Bool
    public var `protocol`: String
    public var actions: [String]
    public var errorCode: String?
    public var weight: Double?
    public var lastUpdateTime: Double?
    public var respondTime: Double?
    public var hasExplore: Bool?
    public var id: String { installationId }
}

/// Runtime locators remain separate from the local UUID. Metadata enrichment or
/// source switching never changes a saved book's identity.
public struct NovelBook: Codable, Equatable, Sendable, Identifiable {
    public var id: String
    public var installationId: String
    public var bookUrl: String
    public var name: String
    public var author: String
    public var tocUrl: String
    public var coverUrl: String
    public var intro: String
    public var latestChapterTitle: String
    private var business = NovelBusinessFields()
    public var variable: RuntimeValue {
        get { business.fields["variable"] ?? .null }
        set { business.fields["variable"] = newValue }
    }
    public init(id: String = UUID().uuidString.lowercased(), installationId: String, bookUrl: String,
                name: String, author: String = "", tocUrl: String = "", coverUrl: String = "",
                intro: String = "", latestChapterTitle: String = "", variable: RuntimeValue = .null) {
        self.id = id; self.installationId = installationId; self.bookUrl = bookUrl
        self.name = name; self.author = author; self.tocUrl = tocUrl; self.coverUrl = coverUrl
        self.intro = intro; self.latestChapterTitle = latestChapterTitle; self.variable = variable
    }
    var wire: RuntimeValue {
        var fields = business.fields
        for (key, value) in RuntimeObject(["installationId": .string(installationId), "bookUrl": .string(bookUrl),
            "name": .string(name), "author": .string(author), "tocUrl": .string(tocUrl), "coverUrl": .string(coverUrl),
            "intro": .string(intro), "latestChapterTitle": .string(latestChapterTitle), "variable": variable]) { fields[key] = value }
        return .object(fields)
    }
    static func parse(_ value: RuntimeValue, source: NovelSource, id: String? = nil, fallback: NovelBook? = nil) throws -> NovelBook {
        guard case .object(let incoming) = value else { throw NovelError.invalidResponse }
        var fields = RuntimeObject()
        if let fallback, case .object(let previous) = fallback.wire { fields = previous }
        for (key, value) in incoming { fields[key] = value }
        let value = RuntimeValue.object(fields)
        guard let url = value["bookUrl"].string, !url.isEmpty else { throw NovelError.invalidResponse }
        var book = NovelBook(id: id ?? source.id + ":" + url, installationId: source.id, bookUrl: url,
            name: value["name"].string ?? "", author: value["author"].string ?? "",
            tocUrl: value["tocUrl"].string ?? url, coverUrl: value["coverUrl"].string ?? "",
            intro: value["intro"].string ?? "", latestChapterTitle: value["latestChapterTitle"].string ?? "")
        book.business.fields = fields
        return book
    }
}

public struct NovelSearchBook: Equatable, Sendable, Identifiable {
    public var book: NovelBook
    public var candidates: [NovelBook]
    public var groupKey: String
    public var id: String { groupKey }
}
public struct NovelChapter: Codable, Equatable, Sendable {
    public var name: String
    public var chapterUrl: String
    public var index: Int
    public var isPay: Bool
    public var isVip: Bool
    public var isVolume: Bool
    private var business = NovelBusinessFields()
    public var variable: RuntimeValue {
        get { business.fields["variable"] ?? .null }
        set { business.fields["variable"] = newValue }
    }
    public init(name: String, chapterUrl: String, index: Int, isPay: Bool = false, isVip: Bool = false, isVolume: Bool = false, variable: RuntimeValue = .null) {
        self.name = name; self.chapterUrl = chapterUrl; self.index = index
        self.isPay = isPay; self.isVip = isVip; self.isVolume = isVolume; self.variable = variable
    }
    var wire: RuntimeValue {
        var fields = business.fields
        for (key, value) in RuntimeObject(["name": .string(name), "chapterUrl": .string(chapterUrl),
            "index": .number(Double(index)), "isPay": .bool(isPay), "isVip": .bool(isVip), "isVolume": .bool(isVolume), "variable": variable]) { fields[key] = value }
        return .object(fields)
    }
    static func parse(_ value: RuntimeValue, index: Int, fallback: NovelChapter? = nil) throws -> NovelChapter {
        guard case .object(let incoming) = value else { throw NovelError.invalidResponse }
        var fields = RuntimeObject()
        if let fallback, case .object(let previous) = fallback.wire { fields = previous }
        for (key, value) in incoming { fields[key] = value }
        let value = RuntimeValue.object(fields)
        guard let url = fallback?.chapterUrl ?? value["chapterUrl"].string else { throw NovelError.invalidResponse }
        var chapter = NovelChapter(name: value["name"].string ?? fallback?.name ?? "", chapterUrl: url, index: index,
            isPay: value["isPay"].bool ?? fallback?.isPay ?? false, isVip: value["isVip"].bool ?? fallback?.isVip ?? false,
            isVolume: value["isVolume"].bool ?? fallback?.isVolume ?? false)
        chapter.business.fields = fields
        return chapter
    }
}

/// Business data returned by info/toc is not a source installation. Keep all
/// rule-visible fields (including nulls) through local persistence and the next
/// content/pay invocation instead of projecting them into a second lossy DTO.
private struct NovelBusinessFields: Codable, Equatable, Sendable {
    var fields = RuntimeObject()
    init() {}
    init(from decoder: Decoder) throws {
        let bytes = try decoder.singleValueContainer().decode(Data.self)
        guard case .object(let fields) = try RuntimeJSON.decode(bytes) else { throw NovelError.invalidResponse }
        self.fields = fields
    }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(RuntimeJSON.encode(.object(fields)))
    }
}
public struct NovelBlock: Codable, Equatable, Sendable {
    public var type: String
    public var text: String?
    public var src: String?
    public var alt: String?
    public init(text: String) { type = "text"; self.text = text }
    public init(src: String, alt: String = "") { type = "image"; self.src = src; self.alt = alt }
}
public struct NovelContent: Codable, Equatable, Sendable {
    public var title: String
    public var blocks: [NovelBlock]
    public var imageStyle: String
    public init(title: String, blocks: [NovelBlock], imageStyle: String = "") {
        self.title = title; self.blocks = blocks; self.imageStyle = imageStyle
    }
}
public struct NovelAnchor: Codable, Equatable, Sendable {
    public var chapterUrl: String
    public var chapterTitle: String
    public var chapterIndex: Int
    public var blockIndex: Int
    public var utf16Offset: Int
    public var ratio: Double
    public init(chapterUrl: String, chapterTitle: String, chapterIndex: Int, blockIndex: Int = 0,
                utf16Offset: Int = 0, ratio: Double = 0) {
        self.chapterUrl = chapterUrl; self.chapterTitle = chapterTitle; self.chapterIndex = chapterIndex
        self.blockIndex = blockIndex; self.utf16Offset = utf16Offset; self.ratio = ratio
    }
}
public struct NovelRule: Codable, Equatable, Sendable, Identifiable {
    public var id: String = UUID().uuidString.lowercased()
    public var name = ""
    public var group = ""
    public var isEnabled = true
    public var order: Int = 0
    public var timeoutMillisecond: Int = 3000
    public var pattern = ""
    public var replacement = ""
    public var isRegex = false
    public var scope = ""
    public var excludeScope = ""
    public var scopeTitle = false
    public var scopeContent = true
    public init() {}
    private enum CodingKeys: String, CodingKey {
        case id, name, group, isEnabled, order, timeoutMillisecond, pattern, replacement, isRegex, scope, excludeScope, scopeTitle, scopeContent
    }
    public init(from decoder: Decoder) throws {
        let fields = try decoder.container(keyedBy: CodingKeys.self)
        if let value = try? fields.decode(String.self, forKey: .id) { id = value }
        else if let value = try? fields.decode(Int.self, forKey: .id) { id = String(value) }
        name = try fields.decodeIfPresent(String.self, forKey: .name) ?? ""
        group = try fields.decodeIfPresent(String.self, forKey: .group) ?? ""
        isEnabled = try fields.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        order = try fields.decodeIfPresent(Int.self, forKey: .order) ?? 0
        timeoutMillisecond = try fields.decodeIfPresent(Int.self, forKey: .timeoutMillisecond) ?? 3000
        pattern = try fields.decode(String.self, forKey: .pattern)
        replacement = try fields.decodeIfPresent(String.self, forKey: .replacement) ?? ""
        isRegex = try fields.decodeIfPresent(Bool.self, forKey: .isRegex) ?? true
        scope = try fields.decodeIfPresent(String.self, forKey: .scope) ?? ""
        excludeScope = try fields.decodeIfPresent(String.self, forKey: .excludeScope) ?? ""
        scopeTitle = try fields.decodeIfPresent(Bool.self, forKey: .scopeTitle) ?? false
        scopeContent = try fields.decodeIfPresent(Bool.self, forKey: .scopeContent) ?? true
    }
}
public enum NovelReadingMode: String, Codable, CaseIterable, Sendable {
    case cover, slide, instant, scroll
    public var title: String { switch self { case .cover: return "覆盖"; case .slide: return "平移"; case .instant: return "无动画"; case .scroll: return "上下滚动" } }
}
public struct NovelReadingSettings: Codable, Equatable, Sendable {
    public var mode: NovelReadingMode = .slide
    public var fontSize: Double = 21
    public var fontWeight: Double = 0
    public var fontName: String = ""
    public var lineSpacing: Double = 8
    public var paragraphSpacing: Double = 16
    public var margin: Double = 22
    public var contentWidth: Double = 680
    public var conversion: String = "none"
    public var theme: Int = 0
    public var foreground: String = ""
    public var background: String = ""
    public var backgroundImage: Data?
    public var automaticSpeed: Double = 4
    public init() {}
    public static let themes: [(name: String, background: String, foreground: String)] = [
        ("纸白", "#F7F4ED", "#292724"), ("米黄", "#EEDDBB", "#403322"),
        ("浅绿", "#DCE8D5", "#25342B"), ("雾蓝", "#DCE6EB", "#293B49"),
        ("浅粉", "#F1DFDE", "#4C3339"), ("浅灰", "#E3E3E3", "#333333"),
        ("深灰", "#303235", "#D0D0D0"), ("夜间", "#171819", "#ADADA9")]
}

extension RuntimeValue {
    subscript(_ key: String) -> RuntimeValue { if case .object(let fields) = self { return fields[key] ?? .null }; return .null }
    var string: String? { if case .string(let value) = self { return value }; return nil }
    var array: [RuntimeValue]? { if case .array(let value) = self { return value }; return nil }
    var bool: Bool? { if case .bool(let value) = self { return value }; return nil }
    var number: Double? { if case .number(let value) = self { return value }; return nil }
    static func encoding<T: Encodable>(_ value: T) throws -> RuntimeValue { try RuntimeJSON.decode(JSONEncoder().encode(value)) }
    func decoding<T: Decodable>(_ type: T.Type) throws -> T { try JSONDecoder().decode(type, from: RuntimeJSON.encode(self)) }
}
