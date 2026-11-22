import Foundation

enum NovelSourceProtocol: String, CaseIterable, Sendable {
    case legado, qysg
    var title: String { switch self { case .legado: return "阅读源"; case .qysg: return "轻悦时光" } }
}
enum NovelSourceFieldKind: Equatable { case text, number, bool, json }
struct NovelSourceField {
    let path: String, label: String
    var rows = 1
    var kind: NovelSourceFieldKind = .text
    var components: [String]? = nil
    var parts: [String] { components ?? path.components(separatedBy: ".") }
    var id: String { parts.map { "\($0.utf8.count):\($0)" }.joined() }
}
struct NovelSourceTab { let title: String; let fields: [NovelSourceField] }
enum NovelSourceFields {
    static let legado: [NovelSourceTab] = [
        .init(title: "基本", fields: [
            .init(path: "bookSourceUrl", label: "源 URL（sourceUrl）", rows: 1, kind: .text),
            .init(path: "bookSourceName", label: "源名称（sourceName）", rows: 1, kind: .text),
            .init(path: "bookSourceGroup", label: "源分组（sourceGroup）", rows: 1, kind: .text),
            .init(path: "bookSourceComment", label: "源注释（sourceComment）", rows: 4, kind: .text),
            .init(path: "loginUrl", label: "登录 URL(loginUrl)", rows: 1, kind: .text),
            .init(path: "loginUi", label: "登录 UI（loginUi）", rows: 1, kind: .text),
            .init(path: "loginCheckJs", label: "登录检查 JS（loginCheckJs）", rows: 1, kind: .text),
            .init(path: "coverDecodeJs", label: "封面解密（coverDecodeJs）", rows: 1, kind: .text),
            .init(path: "bookUrlPattern", label: "书籍 URL 正则（bookUrlPattern）", rows: 1, kind: .text),
            .init(path: "header", label: "请求头（header）", rows: 3, kind: .text),
            .init(path: "variableComment", label: "变量说明(variableComment)", rows: 3, kind: .text),
            .init(path: "concurrentRate", label: "并发率（concurrentRate）", rows: 1, kind: .text),
            .init(path: "jsLib", label: "jsLib", rows: 3, kind: .text),
        ]),
        .init(title: "搜索", fields: [
            .init(path: "searchUrl", label: "搜索地址（url）", rows: 4, kind: .text),
            .init(path: "ruleSearch.checkKeyWord", label: "校验关键字（checkKeyWord）", rows: 1, kind: .text),
            .init(path: "ruleSearch.bookList", label: "书籍列表规则（bookList）", rows: 8, kind: .text),
            .init(path: "ruleSearch.name", label: "书名规则（name）", rows: 1, kind: .text),
            .init(path: "ruleSearch.author", label: "作者规则（author）", rows: 1, kind: .text),
            .init(path: "ruleSearch.kind", label: "分类规则（kind）", rows: 1, kind: .text),
            .init(path: "ruleSearch.wordCount", label: "字数规则（wordCount）", rows: 1, kind: .text),
            .init(path: "ruleSearch.lastChapter", label: "最新章节规则（lastChapter）", rows: 1, kind: .text),
            .init(path: "ruleSearch.intro", label: "简介规则（intro）", rows: 1, kind: .text),
            .init(path: "ruleSearch.coverUrl", label: "封面规则（coverUrl）", rows: 1, kind: .text),
            .init(path: "ruleSearch.bookUrl", label: "详情页 URL 规则（bookUrl）", rows: 1, kind: .text),
        ]),
        .init(title: "发现", fields: [
            .init(path: "exploreUrl", label: "发现地址规则（url）", rows: 12, kind: .text),
            .init(path: "ruleExplore.bookList", label: "书籍列表规则（bookList）", rows: 1, kind: .text),
            .init(path: "ruleExplore.name", label: "书名规则（name）", rows: 1, kind: .text),
            .init(path: "ruleExplore.author", label: "作者规则（author）", rows: 1, kind: .text),
            .init(path: "ruleExplore.kind", label: "分类规则（kind）", rows: 1, kind: .text),
            .init(path: "ruleExplore.wordCount", label: "字数规则（wordCount）", rows: 1, kind: .text),
            .init(path: "ruleExplore.lastChapter", label: "最新章节规则（lastChapter）", rows: 1, kind: .text),
            .init(path: "ruleExplore.intro", label: "简介规则（intro）", rows: 1, kind: .text),
            .init(path: "ruleExplore.coverUrl", label: "封面规则（coverUrl）", rows: 1, kind: .text),
            .init(path: "ruleExplore.bookUrl", label: "详情页 URL 规则（bookUrl）", rows: 1, kind: .text),
        ]),
        .init(title: "详情", fields: [
            .init(path: "ruleBookInfo.init", label: "预处理规则（bookInfoInit）", rows: 3, kind: .text),
            .init(path: "ruleBookInfo.name", label: "书名规则（name）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.author", label: "作者规则（author）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.kind", label: "分类规则（kind）", rows: 3, kind: .text),
            .init(path: "ruleBookInfo.wordCount", label: "字数规则（wordCount）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.lastChapter", label: "最新章节规则（lastChapter）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.intro", label: "简介规则（intro）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.coverUrl", label: "封面规则（coverUrl）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.tocUrl", label: "目录 URL 规则（tocUrl）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.canReName", label: "允许修改书名作者（canReName）", rows: 1, kind: .text),
            .init(path: "ruleBookInfo.downloadUrls", label: "下载URL规则(downloadUrls)", rows: 1, kind: .text),
        ]),
        .init(title: "目录", fields: [
            .init(path: "ruleToc.preUpdateJs", label: "更新之前 JS（preUpdateJs）", rows: 3, kind: .text),
            .init(path: "ruleToc.chapterList", label: "目录列表规则（chapterList）", rows: 1, kind: .text),
            .init(path: "ruleToc.chapterName", label: "章节名称规则（ChapterName）", rows: 1, kind: .text),
            .init(path: "ruleToc.chapterUrl", label: "章节 URL 规则（chapterUrl）", rows: 4, kind: .text),
            .init(path: "ruleToc.formatJs", label: "格式化规则(formatJs)", rows: 3, kind: .text),
            .init(path: "ruleToc.isVolume", label: "Volume 标识（isVolume）", rows: 1, kind: .text),
            .init(path: "ruleToc.updateTime", label: "更新时间（ChapterInfo）", rows: 1, kind: .text),
            .init(path: "ruleToc.isVip", label: "VIP 标识（isVip）", rows: 1, kind: .text),
            .init(path: "ruleToc.isPay", label: "购买标识（isPay）", rows: 1, kind: .text),
            .init(path: "ruleToc.nextTocUrl", label: "目录下一页规则（nextTocUrl）", rows: 1, kind: .text),
        ]),
        .init(title: "正文", fields: [
            .init(path: "ruleContent.content", label: "正文规则（content）", rows: 1, kind: .text),
            .init(path: "ruleContent.title", label: "章节名称规则（ChapterName）", rows: 1, kind: .text),
            .init(path: "ruleContent.nextContentUrl", label: "正文下一页 URL 规则（nextContentUrl）", rows: 1, kind: .text),
            .init(path: "ruleContent.webJs", label: "WebView JS（webJs）", rows: 3, kind: .text),
            .init(path: "ruleContent.sourceRegex", label: "资源正则（sourceRegex）", rows: 3, kind: .text),
            .init(path: "ruleContent.replaceRegex", label: "替换规则（replaceRegex）", rows: 3, kind: .text),
            .init(path: "ruleContent.imageStyle", label: "图片样式（imageStyle）", rows: 3, kind: .text),
            .init(path: "ruleContent.imageDecode", label: "图片解密（imageDecode）", rows: 3, kind: .text),
            .init(path: "ruleContent.payAction", label: "购买操作（payAction）", rows: 3, kind: .text),
        ]),
    ]
    static let qysg: [NovelSourceTab] = [
        .init(title: "基本", fields: [
            .init(path: "bookSourceUrl", label: "源 URL（bookSourceUrl）", rows: 1, kind: .text),
            .init(path: "bookSourceName", label: "源名称（bookSourceName）", rows: 1, kind: .text),
            .init(path: "bookSourceGroup", label: "源分组（bookSourceGroup）", rows: 1, kind: .text),
            .init(path: "bookSourceComment", label: "源注释（bookSourceComment）", rows: 4, kind: .text),
            .init(path: "customOrder", label: "排序（customOrder）", rows: 1, kind: .number),
        ]),
        .init(title: "定义", fields: [
            .init(path: "html", label: "HTML／脚本或远程候选地址（html）", rows: 20, kind: .text),
        ]),
        .init(title: "登录与请求", fields: [
            .init(path: "login", label: "支持登录（login）", rows: 1, kind: .bool),
            .init(path: "header", label: "请求头（header）", rows: 4, kind: .text),
        ]),
    ]
    static func tabs(_ protocol: NovelSourceProtocol) -> [NovelSourceTab] { switch `protocol` { case .legado: return legado; case .qysg: return qysg } }
}
